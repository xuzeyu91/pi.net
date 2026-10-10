using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the shared event bus (port of TS <c>core/event-bus.ts</c>, batch 4d-2a).
/// </summary>
/// <remarks>
/// The TS implementation wraps every handler in an async <c>safeHandler</c> that logs and swallows
/// exceptions, dispatches through Node's <c>EventEmitter</c> (registration order, listener-array
/// clone during emit), and returns an idempotent unsubscribe from <c>on</c>. These tests pin each of
/// those behaviours against the C# port; expectations are transcribed from the TS source and the
/// regression test <c>7193-event-bus-lifecycle.test.ts</c> (emit/on/unsubscribe/clear lifecycle).
/// </remarks>
public class EventBusTests
{
    // ------------------------------------------------------------------ dispatch

    [Fact]
    public void Emit_InvokesHandler_WithTheSameDataReference()
    {
        var bus = EventBusController.CreateEventBus();
        object? received = null;
        var calls = 0;
        bus.On("ch", data =>
        {
            received = data;
            calls++;
            return Task.CompletedTask;
        });

        var payload = new object();
        bus.Emit("ch", payload);

        Assert.Equal(1, calls);
        Assert.Same(payload, received);
    }

    [Fact]
    public void Emit_PassesNullDataThrough()
    {
        var bus = EventBusController.CreateEventBus();
        object? received = new object();
        bus.On("ch", data =>
        {
            received = data;
            return Task.CompletedTask;
        });

        bus.Emit("ch", null);

        Assert.Null(received);
    }

    [Fact]
    public void Emit_InvokesHandlersInRegistrationOrder()
    {
        var bus = EventBusController.CreateEventBus();
        var order = new List<string>();
        bus.On("ch", _ => { order.Add("first"); return Task.CompletedTask; });
        bus.On("ch", _ => { order.Add("second"); return Task.CompletedTask; });
        bus.On("ch", _ => { order.Add("third"); return Task.CompletedTask; });

        bus.Emit("ch", null);

        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public void Emit_OnlyTouchesTheTargetChannel()
    {
        var bus = EventBusController.CreateEventBus();
        var otherCalls = 0;
        bus.On("other", _ => { otherCalls++; return Task.CompletedTask; });

        bus.Emit("ch", null);

        Assert.Equal(0, otherCalls);
    }

    [Fact]
    public void Emit_WithoutListeners_IsANoOp()
    {
        var bus = EventBusController.CreateEventBus();

        bus.Emit("nobody-home", new object());
    }

    [Fact]
    public void Emit_SupportsAsyncHandlers()
    {
        var bus = EventBusController.CreateEventBus();
        var gate = new TaskCompletionSource();
        var observed = false;
        bus.On("ch", async _ =>
        {
            await gate.Task;
            observed = true;
        });

        bus.Emit("ch", null);
        Assert.False(observed); // fire-and-forget: the handler has not run past its await yet
        gate.SetResult();
        Assert.True(SpinUntil(() => observed));
    }

    // ------------------------------------------------------------------ unsubscribe

    [Fact]
    public void On_ReturnsUnsubscribe_ThatStopsFurtherDispatch()
    {
        var bus = EventBusController.CreateEventBus();
        var calls = 0;
        var unsubscribe = bus.On("ch", _ => { calls++; return Task.CompletedTask; });

        bus.Emit("ch", null);
        unsubscribe();
        bus.Emit("ch", null);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Unsubscribe_IsIdempotent_AndDoesNotDisturbOtherSubscriptions()
    {
        var bus = EventBusController.CreateEventBus();
        var a = 0;
        var b = 0;
        var unsubscribeA = bus.On("ch", _ => { a++; return Task.CompletedTask; });
        bus.On("ch", _ => { b++; return Task.CompletedTask; });

        unsubscribeA();
        unsubscribeA(); // second call must be a no-op, like Node's `off`
        bus.Emit("ch", null);

        Assert.Equal(0, a);
        Assert.Equal(1, b);
    }

    [Fact]
    public void SameHandlerRegisteredTwice_GetsTwoIndependentSubscriptions()
    {
        var bus = EventBusController.CreateEventBus();
        var calls = 0;
        Func<object?, Task> handler = _ => { calls++; return Task.CompletedTask; };
        var first = bus.On("ch", handler);
        bus.On("ch", handler);

        bus.Emit("ch", null);
        first();
        bus.Emit("ch", null);

        Assert.Equal(3, calls); // 2 from the first emit, 1 from the second (one subscription left)
    }

    [Fact]
    public void Unsubscribe_DuringDispatch_DoesNotAffectTheInFlightEmit()
    {
        // Node clones the listener array when dispatching, so a handler that unsubscribes a
        // later handler still lets that handler see the current emit.
        var bus = EventBusController.CreateEventBus();
        var secondCalls = 0;
        Action? unsubscribeSecond = null;
        bus.On("ch", _ =>
        {
            unsubscribeSecond?.Invoke();
            return Task.CompletedTask;
        });
        unsubscribeSecond = bus.On("ch", _ => { secondCalls++; return Task.CompletedTask; });

        bus.Emit("ch", null);
        bus.Emit("ch", null);

        Assert.Equal(1, secondCalls); // saw the first emit, gone for the second
    }

    [Fact]
    public void Subscribe_DuringDispatch_DoesNotAffectTheInFlightEmit()
    {
        var bus = EventBusController.CreateEventBus();
        var lateCalls = 0;
        bus.On("ch", _ =>
        {
            bus.On("ch", __ => { lateCalls++; return Task.CompletedTask; });
            return Task.CompletedTask;
        });

        bus.Emit("ch", null);
        Assert.Equal(0, lateCalls); // the new subscription only sees later emits
        bus.Emit("ch", null);
        Assert.Equal(1, lateCalls);
    }

    // ------------------------------------------------------------------ error isolation

    [Fact]
    public void SyncHandlerException_IsLogged_AndDoesNotPropagate()
    {
        var bus = EventBusController.CreateEventBus();
        var stderr = new StringWriter();
        EventBusController.ErrorWriterOverride = stderr;
        try
        {
            bus.On("ch", _ => throw new InvalidOperationException("boom"));

            bus.Emit("ch", null); // must not throw
        }
        finally
        {
            EventBusController.ErrorWriterOverride = null;
        }

        var line = FirstLine(stderr);
        Assert.StartsWith("Event handler error (ch): ", line, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", line, StringComparison.Ordinal);
        Assert.Contains("boom", line, StringComparison.Ordinal);
    }

    [Fact]
    public void HandlerException_DoesNotBlockOtherHandlers()
    {
        var bus = EventBusController.CreateEventBus();
        var stderr = new StringWriter();
        EventBusController.ErrorWriterOverride = stderr;
        try
        {
            var order = new List<string>();
            bus.On("ch", _ =>
            {
                order.Add("thrower");
                throw new InvalidOperationException("boom");
            });
            bus.On("ch", _ => { order.Add("survivor"); return Task.CompletedTask; });

            bus.Emit("ch", null);

            Assert.Equal(["thrower", "survivor"], order);
        }
        finally
        {
            EventBusController.ErrorWriterOverride = null;
        }
    }

    [Fact]
    public async Task AsyncHandlerException_IsLogged_AndDoesNotSurfaceAsUnobserved()
    {
        var bus = EventBusController.CreateEventBus();
        var stderr = new StringWriter();
        EventBusController.ErrorWriterOverride = stderr;
        var release = new TaskCompletionSource();
        try
        {
            bus.On("ch", async _ =>
            {
                await release.Task;
                throw new InvalidOperationException("async boom");
            });

            bus.Emit("ch", null);
            release.SetResult();

            // The wrapper's catch logs after the handler's await resumes.
            Assert.True(SpinUntil(() => stderr.ToString().Contains("async boom", StringComparison.Ordinal)));
            var line = FirstLine(stderr);
            Assert.StartsWith("Event handler error (ch): ", line, StringComparison.Ordinal);
        }
        finally
        {
            EventBusController.ErrorWriterOverride = null;
            await Task.Yield();
        }
    }

    // ------------------------------------------------------------------ clear

    [Fact]
    public void Clear_RemovesListenersOnEveryChannel()
    {
        var bus = EventBusController.CreateEventBus();
        var a = 0;
        var b = 0;
        bus.On("a", _ => { a++; return Task.CompletedTask; });
        bus.On("b", _ => { b++; return Task.CompletedTask; });

        bus.Clear();
        bus.Emit("a", null);
        bus.Emit("b", null);

        Assert.Equal(0, a);
        Assert.Equal(0, b);
    }

    [Fact]
    public void On_AfterClear_RegistersAgain()
    {
        var bus = EventBusController.CreateEventBus();
        var calls = 0;
        bus.On("ch", _ => { calls++; return Task.CompletedTask; });

        bus.Clear();
        bus.On("ch", _ => { calls++; return Task.CompletedTask; });
        bus.Emit("ch", null);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Clear_IsIdempotent()
    {
        var bus = EventBusController.CreateEventBus();
        bus.On("ch", _ => Task.CompletedTask);

        bus.Clear();
        bus.Clear();
    }

    // ------------------------------------------------------------------ factory / contract

    [Fact]
    public void CreateEventBus_ReturnsAController_UsableThroughTheInterface()
    {
        EventBusController controller = EventBusController.CreateEventBus();
        EventBus bus = controller;

        var calls = 0;
        var unsubscribe = bus.On("ch", _ => { calls++; return Task.CompletedTask; });
        bus.Emit("ch", null);
        unsubscribe();
        controller.Clear();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void On_RejectsNullArguments()
    {
        var bus = EventBusController.CreateEventBus();

        Assert.Throws<ArgumentNullException>(() => bus.On(null!, _ => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => bus.On("ch", null!));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// First line of the captured output. <see cref="Exception.ToString()"/> appends the stack trace
    /// on the following lines, so the log line itself is always the first one.
    /// </summary>
    private static string FirstLine(StringWriter writer)
    {
        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines[0];
    }

    /// <summary>Waits for a fire-and-forget continuation to run (emit does not await handlers).</summary>
    private static bool SpinUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (condition())
            {
                return true;
            }

            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            Thread.Sleep(5);
        }
    }
}
