using System.Threading;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 单实例启动：第一个进程持有命名互斥量并监听激活事件；
/// 后续进程不发 UI，只通知第一个进程显示主窗口，然后立即退出。
/// </summary>
internal sealed partial class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\OpenSteamEYA.MainInstance";
    private const string ActivationEventName = @"Local\OpenSteamEYA.ActivateExisting";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly EventWaitHandle? _activationEvent;
    private readonly Thread? _listener;
    private bool _disposed;

    private SingleInstanceService(
        Mutex mutex,
        EventWaitHandle activationEvent,
        Action activationRequested)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        _listener = new Thread(() => ListenForActivation(activationRequested))
        {
            IsBackground = true,
            Name = "SteamEYA single-instance activation"
        };
        _listener.Start();
    }

    /// <summary>
    /// 返回 true 表示当前进程是主实例；false 表示已通知现有实例，当前进程应直接退出。
    /// </summary>
    public static bool TryAcquire(Action activationRequested, out SingleInstanceService? service)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
        service = null;
        if (!createdNew)
        {
            mutex.Dispose();
            SignalExistingInstance();
            return false;
        }

        try
        {
            var activationEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                ActivationEventName);
            service = new SingleInstanceService(mutex, activationEvent, activationRequested);
            return true;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static void SignalExistingInstance()
    {
        // 主实例可能刚拿到互斥量、尚未创建事件；短时间内重试，避免启动竞态导致无法唤起。
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private void ListenForActivation(Action activationRequested)
    {
        if (_activationEvent is null)
        {
            return;
        }

        var handles = new[] { _activationEvent, _cancellation.Token.WaitHandle };
        while (true)
        {
            var signaled = WaitHandle.WaitAny(handles);
            if (signaled != 0 || _cancellation.IsCancellationRequested)
            {
                return;
            }

            try
            {
                activationRequested();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"处理单实例唤醒失败：{ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _activationEvent?.Set();
        _listener?.Join(TimeSpan.FromSeconds(1));
        _activationEvent?.Dispose();
        _cancellation.Dispose();
        _mutex.Dispose();
    }
}
