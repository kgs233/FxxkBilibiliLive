using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Linq;
using System.Threading;

public enum StereoChannel { Left, Right }

static class AudioExtensions
{
    public static ISampleProvider ToSwitchableStereoChannel(this ISampleProvider source, StereoChannel initialChannel)
    {
        return new SwitchableStereoChannelProvider(source, initialChannel);
    }
}

class SwitchableStereoChannelProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private StereoChannel currentChannel;
    private readonly object channelLock = new object();

    public WaveFormat WaveFormat => source.WaveFormat;

    public SwitchableStereoChannelProvider(ISampleProvider source, StereoChannel initialChannel)
    {
        this.source = source;
        currentChannel = initialChannel;
    }

    public void SwitchChannel(StereoChannel newChannel)
    {
        lock (channelLock)
        {
            currentChannel = newChannel;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = source.Read(buffer, offset, count);
        
        lock (channelLock)
        {
            for (int n = 0; n < samplesRead; n += 2)
            {
                switch (currentChannel)
                {
                    case StereoChannel.Left:
                        buffer[offset + n + 1] = 0; // 右声道静音
                        break;
                    case StereoChannel.Right:
                        buffer[offset + n] = 0;    // 左声道静音
                        break;
                }
            }
        }
        return samplesRead;
    }
}

class TimedChannelRouter : IDisposable
{
    private WasapiLoopbackCapture captureDevice;
    private IWavePlayer outputDevice;
    private MediaFoundationResampler resampler;
    private Timer channelTimer;
    private SwitchableStereoChannelProvider channelProvider;
    private StereoChannel currentChannel;
    private int switchInterval;

    public TimedChannelRouter(
        MMDevice sourceOutputDevice,
        MMDevice targetOutputDevice,
        int intervalSeconds)
    {
        currentChannel = StereoChannel.Left;
        switchInterval = intervalSeconds * 1000;

        captureDevice = new WasapiLoopbackCapture(sourceOutputDevice);
        
        var waveProvider = new WaveInProvider(captureDevice);
        var sampleProvider = waveProvider.ToSampleProvider();
        channelProvider = new SwitchableStereoChannelProvider(sampleProvider, currentChannel);
        
        var targetFormat = targetOutputDevice.AudioClient.MixFormat;
        resampler = new MediaFoundationResampler(
            channelProvider.ToWaveProvider(),
            new WaveFormat(targetFormat.SampleRate, targetFormat.BitsPerSample, targetFormat.Channels));

        outputDevice = new WasapiOut(
            targetOutputDevice,
            AudioClientShareMode.Shared,
            false,
            200);
        outputDevice.Init(resampler);

        channelTimer = new Timer(TimerCallback, null, switchInterval, switchInterval);
    }

    private void TimerCallback(object state)
    {
        currentChannel = currentChannel == StereoChannel.Left ? 
            StereoChannel.Right : StereoChannel.Left;
        
        channelProvider.SwitchChannel(currentChannel);
        Console.WriteLine($"[{DateTime.Now:T}] 切换到 {currentChannel} 声道");
    }

    public void Start()
    {
        captureDevice.StartRecording();
        outputDevice.Play();
        Console.WriteLine($"初始声道: {currentChannel}");
    }

    public void Dispose()
    {
        channelTimer?.Dispose();
        captureDevice?.StopRecording();
        outputDevice?.Stop();
        resampler?.Dispose();
        outputDevice?.Dispose();
        captureDevice?.Dispose();
    }
}

class Program
{
    static void Main()
    {
        if (!IsAdministrator())
        {
            Console.WriteLine("请以管理员权限运行此程序！");
            Console.ReadKey();
            return;
        }

        var enumerator = new MMDeviceEnumerator();
        var outputDevices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .ToList();

        Console.WriteLine("系统输出设备列表:");
        for (int i = 0; i < outputDevices.Count; i++)
        {
            Console.WriteLine($"{i}: {outputDevices[i].FriendlyName}");
        }

        int sourceId = GetValidInput("选择源设备", 0, outputDevices.Count - 1);
        int targetId = GetValidInput("选择目标设备", 0, outputDevices.Count - 1);
        int interval = GetValidInput("输入切换间隔（秒）", 1, 3600);

        try
        {
            using var router = new TimedChannelRouter(
                outputDevices[sourceId],
                outputDevices[targetId],
                interval);
            
            router.Start();
            Console.WriteLine($"\n每 {interval} 秒自动切换声道...");
            Console.WriteLine("按 Q 键停止程序");
            
            while (Console.ReadKey(true).Key != ConsoleKey.Q) ;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"错误: {ex.Message}");
        }
    }

    static bool IsAdministrator()
    {
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    static int GetValidInput(string prompt, int min, int max)
    {
        int result;
        do
        {
            Console.Write($"{prompt} ({min}-{max}): ");
        } while (!int.TryParse(Console.ReadLine(), out result) || result < min || result > max);
        return result;
    }
}