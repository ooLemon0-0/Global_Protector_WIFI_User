using System;
using GlobalProtectorWiFi;
class InputTests {
    static int Main() {
        int count=0;
        foreach(string input in new[]{"192.0.2.1","192.0.2.1,192.0.2.2","192.0.2.1，192.0.2.2","192.0.2.1\r\n192.0.2.2"}) { if(InputRules.Parse(input,false).Length<1) throw new Exception(input); count++; }
        foreach(string input in new[]{"","0.0.0.0","127.0.0.1","224.0.0.1","255.255.255.255","::1","192.0.2.999","192.0.2.1/24","192.0.2.1\" -Command calc","192.0.2.1;Write-Host","01.2.3.4"}) {
            bool rejected=false; try{InputRules.Parse(input,false);} catch(ArgumentException){rejected=true;} if(!rejected) throw new Exception("Unsafe input: "+input); count++;
        }
        if(InputRules.Parse("192.0.2.1,192.0.2.1",false).Length!=1) throw new Exception("Deduplication"); count++;
        for(int bits=0;bits<16;bits++) {
            bool armed=(bits&1)!=0, stopping=(bits&2)!=0, error=(bits&4)!=0, pending=(bits&8)!=0;
            if(Lifecycle.ShouldRearm(armed,stopping,error,pending)!=(armed && !stopping && !error && !pending)) throw new Exception("Reconnect lifecycle");
        }
        Console.WriteLine("PASS: 16 reconnect / explicit stop / error / recovery lifecycle combinations.");
        Console.WriteLine("PASS: "+count+" frontend validation cases."); return 0;
    }
}
