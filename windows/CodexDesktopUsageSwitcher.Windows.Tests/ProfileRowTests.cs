using System.Reflection;
using System.Runtime.ExceptionServices;
using CodexDesktopUsageSwitcher.Windows.UI.Controls;
using Xunit;

namespace CodexDesktopUsageSwitcher.Windows.Tests;

public sealed class ProfileRowTests
{
    [Fact]
    public void OwnClick_RaisesExternalClickOnlyOnce()
    {
        RunOnSta(() =>
        {
            using var row = new ProfileRow("main");
            var clicks = 0;
            row.Click += (_, _) => clicks++;

            RaiseClick(row);

            Assert.Equal(1, clicks);
        });
    }

    [Fact]
    public void ChildClick_RaisesRowClickOnlyOnce()
    {
        RunOnSta(() =>
        {
            using var row = new ProfileRow("main");
            var clicks = 0;
            row.Click += (_, _) => clicks++;

            RaiseClick(row.Controls[0]);

            Assert.Equal(1, clicks);
        });
    }

    private static void RaiseClick(Control control)
    {
        var onClick = typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(Control), "OnClick");
        onClick.Invoke(control, [EventArgs.Empty]);
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
