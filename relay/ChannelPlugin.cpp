#define NOMINMAX
#include <windows.h>
#include <tsvirtualchannels.h>
#include <atomic>
#include <cstdio>
#include <cwchar>
#include <deque>
#include <mutex>
#include <new>
#include <vector>

#include "Session.h"

constexpr DWORD FrameLimit = 1200;

// Diagnostics contain lifecycle stages and error codes only. They must never
// affect a connection, retain a file handle or replace the caller's LastError.
static void Trace(const char* stage, DWORD value = 0) noexcept {
    struct ErrorGuard { DWORD saved = GetLastError(); ~ErrorGuard() { SetLastError(saved); } } guard;
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&Trace), &module)) return;
    wchar_t path[MAX_PATH];
    DWORD length = GetModuleFileNameW(module, path, MAX_PATH);
    if (!length || length >= MAX_PATH) return;
    wchar_t* separator = wcsrchr(path, L'\\');
    if (!separator) return;
    if (wcscpy_s(separator + 1, MAX_PATH - (separator + 1 - path), L"runtime\\plugin.log")) return;
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    SYSTEMTIME now{}; GetLocalTime(&now);
    char line[256];
    int count = sprintf_s(line, "%04u-%02u-%02uT%02u:%02u:%02u.%03u pid=%lu tid=%lu %s value=0x%08lx\r\n",
        now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds,
        GetCurrentProcessId(), GetCurrentThreadId(), stage, value);
    DWORD written = 0;
    if (count > 0) WriteFile(file, line, static_cast<DWORD>(count), &written, nullptr);
    CloseHandle(file);
}

class ChannelBridge final : public IWTSVirtualChannelCallback {
    std::atomic<ULONG> refs{1};
    std::atomic<bool> stopped{false};
    IWTSVirtualChannel* channel;
    std::mutex queueLock;
    std::deque<std::vector<BYTE>> incoming;

    static DWORD WINAPI RunThread(void* context) {
        auto* bridge = static_cast<ChannelBridge*>(context);
        bridge->Run();
        bridge->Release();
        return 0;
    }

    void Run() {
        // This worker can still be active briefly after the RDP session closes.
        HANDLE pipe = CreateFileW(PipePath, GENERIC_READ | GENERIC_WRITE, 0,
                                  nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
        Trace(pipe == INVALID_HANDLE_VALUE ? "PIPE_OPEN_FAILED" : "PIPE_OPEN", pipe == INVALID_HANDLE_VALUE ? GetLastError() : 0);
        if (pipe != INVALID_HANDLE_VALUE) {
            DWORD mode = PIPE_READMODE_MESSAGE;
            if (SetNamedPipeHandleState(pipe, &mode, nullptr, nullptr)) { Trace("PIPE_MESSAGE_MODE"); Bridge(pipe); }
            else Trace("PIPE_MODE_FAILED", GetLastError());
            CloseHandle(pipe);
        }
        Trace("BRIDGE_ENDED");
        if (!stopped.exchange(true)) channel->Close();
    }

    bool Transfer(HANDLE pipe, bool write, BYTE* buffer, DWORD size, DWORD& done) {
        OVERLAPPED op{};
        op.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!op.hEvent) return false;
        BOOL ok = write ? WriteFile(pipe, buffer, size, &done, &op)
                        : ReadFile(pipe, buffer, size, &done, &op);
        if (!ok && GetLastError() == ERROR_IO_PENDING) {
            if (WaitForSingleObject(op.hEvent, 3000) != WAIT_OBJECT_0) CancelIoEx(pipe, &op);
            ok = GetOverlappedResult(pipe, &op, &done, TRUE);
        }
        CloseHandle(op.hEvent);
        return ok != FALSE;
    }

    void Bridge(HANDLE pipe) {
        BYTE buffer[FrameLimit];
        while (!stopped.load()) {
            bool worked = false;
            DWORD available = 0, count = 0;
            if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) return;
            if (available) {
                if (!Transfer(pipe, false, buffer, sizeof buffer, count) || !count) return;
                if (FAILED(channel->Write(count, buffer, nullptr))) return;
                worked = true;
            }
            std::vector<BYTE> message;
            {
                std::lock_guard<std::mutex> lock(queueLock);
                if (!incoming.empty()) {
                    message.swap(incoming.front());
                    incoming.pop_front();
                }
            }
            if (!message.empty()) {
                if (!Transfer(pipe, true, message.data(), static_cast<DWORD>(message.size()), count) || count != message.size()) return;
                worked = true;
            }
            if (!worked) Sleep(5);
        }
    }

public:
    explicit ChannelBridge(IWTSVirtualChannel* connection) : channel(connection) { channel->AddRef(); }
    ~ChannelBridge() { channel->Release(); }
    bool Start() {
        HMODULE library = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                                reinterpret_cast<LPCWSTR>(&RunThread), &library)) return false;
        AddRef();
        HANDLE worker = CreateThread(nullptr, 0, &RunThread, this, 0, nullptr);
        if (!worker) { Release(); return false; }
        CloseHandle(worker);
        return true;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id != IID_IUnknown && id != __uuidof(IWTSVirtualChannelCallback)) return E_NOINTERFACE;
        *result = static_cast<IWTSVirtualChannelCallback*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG left = --refs; if (!left) delete this; return left; }
    HRESULT STDMETHODCALLTYPE OnDataReceived(ULONG bytes, BYTE* data) override {
        if (stopped.load()) return S_OK;
        if (!data || bytes < 5 || bytes > FrameLimit) return E_INVALIDARG;
        try {
            std::lock_guard<std::mutex> lock(queueLock);
            if (incoming.size() >= 1024) return HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_MEMORY);
            incoming.emplace_back(data, data + bytes);
            return S_OK;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE OnClose() override { Trace("CHANNEL_CLOSED"); stopped.store(true); return S_OK; }
};

class Plugin final : public IWTSPlugin, public IWTSListenerCallback {
    std::atomic<ULONG> refs{1};
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id == IID_IUnknown || id == __uuidof(IWTSPlugin)) *result = static_cast<IWTSPlugin*>(this);
        else if (id == __uuidof(IWTSListenerCallback)) *result = static_cast<IWTSListenerCallback*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG left = --refs; if (!left) delete this; return left; }
    HRESULT STDMETHODCALLTYPE Initialize(IWTSVirtualChannelManager* manager) override {
        Trace("INITIALIZE");
        if (!manager) return E_POINTER;
        IWTSListener* listener = nullptr;
        HRESULT result = manager->CreateListener(ChannelName, 0, this, &listener);
        Trace("CREATE_LISTENER", static_cast<DWORD>(result));
        if (listener) listener->Release();
        return result;
    }
    HRESULT STDMETHODCALLTYPE Connected() override { Trace("CONNECTED"); return S_OK; }
    HRESULT STDMETHODCALLTYPE Disconnected(DWORD reason) override { Trace("DISCONNECTED", reason); return S_OK; }
    HRESULT STDMETHODCALLTYPE Terminated() override { Trace("TERMINATED"); return S_OK; }
    HRESULT STDMETHODCALLTYPE OnNewChannelConnection(IWTSVirtualChannel* channel, BSTR, BOOL* accept,
                                                      IWTSVirtualChannelCallback** callback) override {
        Trace("NEW_CHANNEL_REQUEST");
        if (!accept || !callback) return E_POINTER;
        *accept = FALSE; *callback = nullptr;
        if (!channel) return E_POINTER;
        if (!WaitNamedPipeW(PipePath, 0)) { Trace("PIPE_UNAVAILABLE", GetLastError()); return S_OK; }
        auto* bridge = new(std::nothrow) ChannelBridge(channel);
        if (!bridge) return E_OUTOFMEMORY;
        if (!bridge->Start()) { bridge->Release(); return E_FAIL; }
        *callback = bridge; *accept = TRUE;
        Trace("CHANNEL_ACCEPTED");
        return S_OK;
    }
};

extern "C" __declspec(dllexport) HRESULT WINAPI VirtualChannelGetInstance(REFIID id, ULONG* count, void** objects) {
    Trace(objects ? "GET_PLUGIN_INSTANCE" : "GET_PLUGIN_COUNT");
    if (!count) return E_POINTER;
    if (id != __uuidof(IWTSPlugin)) return E_NOINTERFACE;
    if (!objects) { *count = 1; return S_OK; }
    if (*count < 1) return E_INVALIDARG;
    *count = 0;
    auto* plugin = new(std::nothrow) Plugin;
    if (!plugin) return E_OUTOFMEMORY;
    objects[0] = static_cast<IWTSPlugin*>(plugin); *count = 1;
    return S_OK;
}
