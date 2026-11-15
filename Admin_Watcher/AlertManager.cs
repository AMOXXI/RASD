using System;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RASD_AdminWatcher
{
    public enum AlertType { Info, Warning, Error }

    public class AlertManager
    {
        private readonly StackPanel   _panel;
        private readonly Border       _container;
        private const int MAX_ALERTS = 5;

        public AlertManager(Border container, StackPanel panel)
        {
            _container = container;
            _panel     = panel;
        }

        // ── Show a non-blocking alert ──────────────────────────────────────
        public void Show(AlertType type, string title, string message,
            string suggestion = null,
            string retryLabel = null,
            Action onRetry    = null)
        {
            _panel.Dispatcher.Invoke(() =>
            {
                // Drop oldest if full
                while (_panel.Children.Count >= MAX_ALERTS)
                    _panel.Children.RemoveAt(0);

                _panel.Children.Add(BuildCard(type, title, message, suggestion, retryLabel, onRetry));
                _container.Visibility = Visibility.Visible;

                ErrorLogger.Write(
                    type == AlertType.Error   ? "ERROR" :
                    type == AlertType.Warning ? "WARN"  : "INFO",
                    title + " — " + message);
            });
        }

        // ── Clear all visible alerts ───────────────────────────────────────
        public void Clear()
        {
            _panel.Dispatcher.Invoke(() =>
            {
                _panel.Children.Clear();
                _container.Visibility = Visibility.Collapsed;
            });
        }

        // ── Connectivity diagnostic: ping a client, then show result ───────
        public async Task DiagnoseClientAsync(string clientIp, string disconnectReason)
        {
            bool pingOk = await Task.Run(() =>
            {
                try
                {
                    var reply = new Ping().Send(clientIp, 3000);
                    return reply?.Status == IPStatus.Success;
                }
                catch { return false; }
            });

            if (!pingOk)
            {
                Show(AlertType.Error,
                    title:      "Client Unreachable — " + clientIp,
                    message:    "Connection lost: " + disconnectReason +
                                ". ICMP ping also failed.",
                    suggestion: "Check network cable / Wi-Fi on the client machine. " +
                                "Ensure client is powered on and connected to the same subnet.");
            }
            else
            {
                Show(AlertType.Warning,
                    title:      "Client Disconnected — " + clientIp,
                    message:    "TCP relay disconnected (" + disconnectReason +
                                "), but the machine is still reachable by ping.",
                    suggestion: "The RASD_Connector service may have stopped. " +
                                "On the client: run 'sc start RASD_Enterprise_Connector' or reinstall.");
            }
        }

        // ── Build one alert card (pure code, no XAML) ─────────────────────
        private Border BuildCard(AlertType type, string title, string message,
            string suggestion, string retryLabel, Action onRetry)
        {
            Color border, bg, accent;
            string icon;
            switch (type)
            {
                case AlertType.Error:
                    border = Color.FromArgb(0x60, 0xEF, 0x44, 0x44);
                    bg     = Color.FromArgb(0x18, 0xEF, 0x44, 0x44);
                    accent = Color.FromRgb(0xEF, 0x44, 0x44);
                    icon   = "✗";
                    break;
                case AlertType.Warning:
                    border = Color.FromArgb(0x60, 0xEA, 0xB3, 0x08);
                    bg     = Color.FromArgb(0x18, 0xEA, 0xB3, 0x08);
                    accent = Color.FromRgb(0xEA, 0xB3, 0x08);
                    icon   = "⚠";
                    break;
                default:
                    border = Color.FromArgb(0x60, 0x3B, 0x82, 0xF6);
                    bg     = Color.FromArgb(0x18, 0x3B, 0x82, 0xF6);
                    accent = Color.FromRgb(0x60, 0xA5, 0xFA);
                    icon   = "i";
                    break;
            }

            var card = new Border
            {
                Background      = new SolidColorBrush(bg),
                BorderBrush     = new SolidColorBrush(border),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Margin          = new Thickness(0, 0, 0, 5),
                Padding         = new Thickness(12, 9, 12, 9),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Icon
            var iconTxt = new TextBlock
            {
                Text              = icon,
                FontFamily        = new FontFamily("Consolas"),
                FontSize          = 13,
                FontWeight        = FontWeights.Bold,
                Foreground        = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Top,
                Margin            = new Thickness(0, 1, 10, 0),
            };
            Grid.SetColumn(iconTxt, 0);
            grid.Children.Add(iconTxt);

            // Content
            var content = new StackPanel();
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);

            content.Children.Add(new TextBlock
            {
                Text       = title,
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent),
            });

            content.Children.Add(new TextBlock
            {
                Text         = message,
                FontFamily   = new FontFamily("Consolas"),
                FontSize     = 10,
                Foreground   = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 3, 0, 0),
            });

            if (!string.IsNullOrEmpty(suggestion))
            {
                content.Children.Add(new TextBlock
                {
                    Text         = "→ " + suggestion,
                    FontFamily   = new FontFamily("Consolas"),
                    FontSize     = 9,
                    Foreground   = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 4, 0, 0),
                });
            }

            if (!string.IsNullOrEmpty(retryLabel) && onRetry != null)
            {
                var retryBtn = new Button
                {
                    Content         = retryLabel,
                    FontFamily      = new FontFamily("Consolas"),
                    FontSize        = 9,
                    Foreground      = new SolidColorBrush(accent),
                    Background      = Brushes.Transparent,
                    BorderBrush     = new SolidColorBrush(border),
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(10, 3, 10, 3),
                    Margin          = new Thickness(0, 7, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Cursor          = System.Windows.Input.Cursors.Hand,
                };
                var capturedCard = card;
                retryBtn.Click += (s, e) =>
                {
                    onRetry?.Invoke();
                    _panel.Children.Remove(capturedCard);
                    if (_panel.Children.Count == 0) _container.Visibility = Visibility.Collapsed;
                };
                content.Children.Add(retryBtn);
            }

            // Dismiss ×
            var dismissBtn = new Button
            {
                Content         = "×",
                FontFamily      = new FontFamily("Consolas"),
                FontSize        = 15,
                Foreground      = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding         = new Thickness(4, 0, 0, 4),
                VerticalAlignment = VerticalAlignment.Top,
                Cursor          = System.Windows.Input.Cursors.Hand,
            };
            var capturedCard2 = card;
            dismissBtn.Click += (s, e) =>
            {
                _panel.Children.Remove(capturedCard2);
                if (_panel.Children.Count == 0) _container.Visibility = Visibility.Collapsed;
            };
            Grid.SetColumn(dismissBtn, 2);
            grid.Children.Add(dismissBtn);

            card.Child = grid;
            return card;
        }
    }
}
