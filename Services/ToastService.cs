using Microsoft.JSInterop;

namespace Chandlery.Services;

/// <summary>Shows a small notification in the corner of the screen (implemented in wwwroot/js/app.js).</summary>
public class ToastService(IJSRuntime js)
{
    public Task Success(string message) => Show("success", message);
    public Task Error(string message) => Show("danger", message);
    public Task Info(string message) => Show("info", message);

    private async Task Show(string tone, string message)
    {
        try
        {
            await js.InvokeVoidAsync("chandlery.toast", tone, message);
        }
        catch (InvalidOperationException) { /* prerendering: no browser yet */ }
        catch (JSDisconnectedException) { /* circuit gone */ }
    }
}
