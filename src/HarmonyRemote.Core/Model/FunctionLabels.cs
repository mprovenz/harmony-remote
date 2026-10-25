using System.Text;

namespace HarmonyRemote.Core.Model;

/// <summary>
/// Readable key labels. Standard functions arrive with spaced labels ("Volume Down"), but many device functions carry
/// the raw command name ("InputHdmi1", "AnalogBalanced", "InputPc-Usb").
/// </summary>
public static class FunctionLabels
{
    private static readonly Dictionary<string, string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hdmi"] = "HDMI", ["usb"] = "USB", ["av"] = "AV", ["tv"] = "TV", ["cd"] = "CD", ["dvd"] = "DVD", ["pc"] = "PC",
        ["xlr"] = "XLR", ["arc"] = "ARC", ["coax"] = "Coax", ["aes"] = "AES", ["ebu"] = "EBU", ["am"] = "AM", ["fm"] = "FM",
        ["dvr"] = "DVR", ["vcr"] = "VCR", ["hd"] = "HD", ["bt"] = "BT",
    };

    /// <summary>Splits run-together words and numbers and fixes acronyms; labels that already contain a space are kept.</summary>
    public static string Humanize(string label)
    {
        if (label.Length == 0 || label.Contains(' '))
            return label;

        var words = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < label.Length; i++)
        {
            char c = label[i];
            if (c is '-' or '/' or '_')
            {
                Flush();
                words.Add(c == '_' ? " " : c.ToString());
                continue;
            }
            if (current.Length > 0)
            {
                char prev = current[^1];
                bool lowerToUpper = char.IsLower(prev) && char.IsUpper(c);
                bool letterToDigit = char.IsLetter(prev) && char.IsDigit(c);
                // "HDMIInput": an upper run followed by Upper+lower starts a new word at the last upper letter.
                bool acronymEnd = char.IsUpper(prev) && char.IsUpper(c) && i + 1 < label.Length && char.IsLower(label[i + 1]);
                if (lowerToUpper || letterToDigit || acronymEnd)
                    Flush();
            }
            current.Append(c);
        }
        Flush();

        var sb = new StringBuilder();
        for (int i = 0; i < words.Count; i++)
        {
            string w = words[i];
            bool isSeparator = w is "-" or "/" or " ";
            bool previousIsSeparator = i > 0 && words[i - 1] is "-" or "/" or " ";
            if (i > 0 && !isSeparator && !previousIsSeparator)
                sb.Append(' ');
            sb.Append(isSeparator ? (w == " " ? " " : w) : Acronyms.GetValueOrDefault(w, w));
        }
        return sb.ToString();

        void Flush()
        {
            if (current.Length > 0)
                words.Add(current.ToString());
            current.Clear();
        }
    }

    /// <summary>Whether a function selects an input ("Input1", "InputHdmi1", "InputNext").</summary>
    public static bool IsInput(HubFunction function) =>
        function.Name.StartsWith("Input", StringComparison.OrdinalIgnoreCase) && function.Name.Length > "Input".Length;

    /// <summary>The key text under an "Inputs" caption: "HDMI 1", "Optical 2", "Next"; bare numbers stay "Input 1".</summary>
    public static string InputKeyLabel(HubFunction function)
    {
        string rest = function.Name["Input".Length..].TrimStart(' ', '-', '_');
        if (rest.Length == 0 || rest.All(char.IsDigit))
            return Humanize(function.Name);
        return Humanize(rest);
    }
}
