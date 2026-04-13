using System.Media;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ImageTagger.Services;

/// <summary>
/// Handles local (taskbar / sound) and remote (Discord webhook) notifications.
/// </summary>
public static class NotificationService
{
    // ── HTTP client (shared for the process lifetime) ─────────────────────────

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    // ── Win32 taskbar flash ───────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint   cbSize;
        public IntPtr hwnd;
        public uint   dwFlags;
        public uint   uCount;
        public uint   dwTimeout;
    }

    private const uint FLASHW_ALL       = 3;   // flash caption + taskbar button
    private const uint FLASHW_TIMERNOFG = 12;  // keep flashing until window is foregrounded

    /// <summary>
    /// Flashes the main window's taskbar button and plays the system exclamation sound.
    /// Safe to call from any thread — marshals to the UI thread internally.
    /// </summary>
    public static void NotifyComplete(Window window)
    {
        // Play sound (works on any thread)
        try { SystemSounds.Exclamation.Play(); } catch { /* ignore */ }

        // Flash taskbar (needs HWND, so dispatch to UI thread)
        window.Dispatcher.Invoke(() =>
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero) return;

            var info = new FLASHWINFO
            {
                cbSize    = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd      = helper.Handle,
                dwFlags   = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount    = 5,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        });
    }

    // ── Discord webhook ───────────────────────────────────────────────────────

    /// <summary>
    /// Validates a Discord webhook URL by issuing an HTTP GET.
    /// Discord returns 200 + JSON metadata for a valid webhook.
    /// </summary>
    public static async Task<bool> ValidateWebhookAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            var response = await _http.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Posts a plain-text message to a Discord webhook.
    /// Failures are swallowed silently — the app should never crash over a notification.
    /// </summary>
    public static async Task SendDiscordMessageAsync(string webhookUrl, string message)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl) ||
            string.IsNullOrWhiteSpace(message))
            return;
        try
        {
            var payload = new { content = message };
            await _http.PostAsJsonAsync(webhookUrl, payload);
        }
        catch { /* best-effort */ }
    }
}
