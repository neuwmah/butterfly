namespace Butterfly.Models
{
    public class Language
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Flag { get; set; } = string.Empty;
        public string DisplayText { get; set; } = string.Empty;
        public string FlagPath { get; set; } = string.Empty;

        public Language(string code, string name, string flag, string displayText)
        {
            Code = code;
            Name = name;
            Flag = flag;
            DisplayText = displayText;
        }

        public Language(string code, string name, string flag, string displayText, string flagPath)
        {
            Code = code;
            Name = name;
            Flag = flag;
            DisplayText = displayText;
            FlagPath = flagPath;
        }
    }
}
