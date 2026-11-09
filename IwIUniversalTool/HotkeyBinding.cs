using System;
using System.Windows.Input;

namespace IwIUniversalTool
{
    /// <summary>
    /// Привязка горячей клавиши/кнопки мыши: "Key:C", "Mouse:Left", "Mouse:Right",
    /// "Mouse:Middle", "Mouse:X1", "Mouse:X2", "Mouse:WheelUp", "Mouse:WheelDown".
    /// </summary>
    public static class HotkeyBinding
    {
        public const string DefaultReplace = "Key:C";

        public static string KeyToken(Key key) => "Key:" + key;

        public static string MouseToken(MouseButton button) => button switch
        {
            MouseButton.Left => "Mouse:Left",
            MouseButton.Right => "Mouse:Right",
            MouseButton.Middle => "Mouse:Middle",
            MouseButton.XButton1 => "Mouse:X1",
            MouseButton.XButton2 => "Mouse:X2",
            _ => "Mouse:Left"
        };

        public static bool MatchesKey(string? binding, Key key)
            => !string.IsNullOrEmpty(binding)
               && binding.StartsWith("Key:", StringComparison.OrdinalIgnoreCase)
               && string.Equals(binding.Substring(4), key.ToString(), StringComparison.OrdinalIgnoreCase);

        public static bool MatchesMouse(string? binding, MouseButton button)
            => !string.IsNullOrEmpty(binding)
               && string.Equals(binding, MouseToken(button), StringComparison.OrdinalIgnoreCase);

        public static bool MatchesWheel(string? binding, int delta)
            => delta != 0 && string.Equals(binding, delta > 0 ? "Mouse:WheelUp" : "Mouse:WheelDown", StringComparison.OrdinalIgnoreCase);

        /// <summary>Короткая подпись для кнопки в настройках и подсказок.</summary>
        public static string Display(string? binding, bool ru)
        {
            if (string.IsNullOrWhiteSpace(binding)) return "—";

            if (binding.StartsWith("Key:", StringComparison.OrdinalIgnoreCase))
                return binding.Substring(4);

            return binding.ToLowerInvariant() switch
            {
                "mouse:left" => ru ? "ЛКМ" : "LMB",
                "mouse:right" => ru ? "ПКМ" : "RMB",
                "mouse:middle" => ru ? "СКМ" : "MMB",
                "mouse:x1" => ru ? "Боковая 1" : "Mouse 4",
                "mouse:x2" => ru ? "Боковая 2" : "Mouse 5",
                "mouse:wheelup" => ru ? "Колесо вверх" : "Wheel up",
                "mouse:wheeldown" => ru ? "Колесо вниз" : "Wheel down",
                _ => binding
            };
        }
    }
}
