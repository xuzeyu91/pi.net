namespace Pi.Chord.Services;

/// <summary>
/// 进程内回环传输：把消费者绑定直接接到同一个进程的 <see cref="RemoteServiceProvider"/>。
/// 对应 TS <c>services/loopback.ts</c> 的 <c>createLoopbackServiceTransport</c>。
/// </summary>
public static class LoopbackServiceTransport
{
    public static IRemoteServiceTransport Create(RemoteServiceProvider provider) => new Loopback(provider);

    private sealed class Loopback(RemoteServiceProvider provider) : IRemoteServiceTransport
    {
        public Task<object?> InvokeAsync(ServiceCall call, Context.Context context)
            => provider.Invoke(call, context);

        public Task<IRemoteServiceSubscription> SubscribeAsync(string serviceId, ServiceMode mode,
            Func<ServiceProviderUpdate, Context.Context, Task?> listener, Context.Context context)
        {
            var handle = provider.Subscribe(serviceId, mode, (update, updateContext) =>
            {
                // provider 的监听是同步的；把异步监听排到后台尽力而为（对齐 TS 的 fire-and-forget）。
                var task = listener(update, updateContext);
                if (task is not null && !task.IsCompleted)
                {
                    _ = task.ContinueWith(
                        _ => { }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            });
            return Task.FromResult<IRemoteServiceSubscription>(new LoopbackSubscription(handle));
        }
    }

    private sealed class LoopbackSubscription(ServiceSubscriptionSnapshotHandle handle) : IRemoteServiceSubscription
    {
        public ServiceSubscriptionSnapshot Snapshot => handle.Snapshot;

        public void Activate() => handle.Activate();

        public Task CloseAsync(Context.Context context)
        {
            handle.Close();
            return Task.CompletedTask;
        }
    }
}
