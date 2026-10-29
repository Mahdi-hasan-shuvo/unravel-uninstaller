// Busy-loops on most cores for a limited time; used to trigger the live monitor's sustained-CPU alert.
using System;
using System.Threading;

static class CpuBurner
{
    static void Main(string[] args)
    {
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 90;
        int threads = Math.Max(1, Environment.ProcessorCount * 3 / 4);
        var end = DateTime.UtcNow.AddSeconds(seconds);
        for (int i = 0; i < threads; i++)
            new Thread(() => { double x = 0; while (DateTime.UtcNow < end) x += Math.Sqrt(x + 1); }) { IsBackground = true }.Start();
        while (DateTime.UtcNow < end) Thread.Sleep(500);
    }
}
