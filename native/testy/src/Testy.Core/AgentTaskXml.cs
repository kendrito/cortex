using System.Text;
using System.Xml;

namespace Testy.Core;

/// <summary>Task Scheduler definition that starts the agent in this user's interactive session at sign-in, with the user's highest privileges.</summary>
public static class AgentTaskXml
{
    public const string TaskFolder = @"\Testy\";
    private const string Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    public static string TaskName(string userSid) => ValidSid(userSid) ? "Testy Agent " + userSid : throw new InvalidDataException("A Windows user SID is required.");
    public static string TaskPath(string userSid) => TaskFolder + TaskName(userSid);

    public static string Build(string userSid, string executable, string arguments, string workingDirectory)
    {
        if (!ValidSid(userSid)) throw new InvalidDataException("A Windows user SID is required.");
        if (!Path.IsPathFullyQualified(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The agent executable must be an absolute .exe path.");
        if (!Path.IsPathFullyQualified(workingDirectory)) throw new InvalidDataException("The agent working directory must be absolute.");
        if (arguments.Length > 4000 || arguments.Any(char.IsControl)) throw new InvalidDataException("Agent arguments are invalid.");
        var text = new StringBuilder();
        using (var xml = XmlWriter.Create(text, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false, Encoding = Encoding.Unicode }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("Task", Namespace); xml.WriteAttributeString("version", "1.4");
            xml.WriteStartElement("RegistrationInfo", Namespace);
            Element(xml, "Author", "Testy");
            Element(xml, "Description", "Starts the Testy background agent at sign-in. It runs queued and scheduled Testy tests on this desktop and in set-up Hyper-V VMs.");
            Element(xml, "URI", TaskPath(userSid));
            xml.WriteEndElement();
            xml.WriteStartElement("Triggers", Namespace);
            xml.WriteStartElement("LogonTrigger", Namespace); Element(xml, "Enabled", "true"); Element(xml, "UserId", userSid); Element(xml, "Delay", "PT30S"); xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteStartElement("Principals", Namespace);
            xml.WriteStartElement("Principal", Namespace); xml.WriteAttributeString("id", "Author");
            Element(xml, "UserId", userSid); Element(xml, "LogonType", "InteractiveToken"); Element(xml, "RunLevel", "HighestAvailable");
            xml.WriteEndElement(); xml.WriteEndElement();
            xml.WriteStartElement("Settings", Namespace);
            Element(xml, "MultipleInstancesPolicy", "IgnoreNew");
            Element(xml, "DisallowStartIfOnBatteries", "false");
            Element(xml, "StopIfGoingOnBatteries", "false");
            Element(xml, "AllowHardTerminate", "true");
            Element(xml, "StartWhenAvailable", "false");
            Element(xml, "RunOnlyIfNetworkAvailable", "false");
            Element(xml, "AllowStartOnDemand", "true");
            Element(xml, "Enabled", "true");
            Element(xml, "Hidden", "false");
            Element(xml, "RunOnlyIfIdle", "false");
            Element(xml, "WakeToRun", "false");
            Element(xml, "ExecutionTimeLimit", "PT0S");
            Element(xml, "Priority", "5");
            xml.WriteStartElement("RestartOnFailure", Namespace); Element(xml, "Interval", "PT1M"); Element(xml, "Count", "3"); xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteStartElement("Actions", Namespace); xml.WriteAttributeString("Context", "Author");
            xml.WriteStartElement("Exec", Namespace); Element(xml, "Command", executable); Element(xml, "Arguments", arguments); Element(xml, "WorkingDirectory", workingDirectory); xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement(); xml.WriteEndDocument();
        }
        return text.ToString();
    }
    /// <summary>Quotes one Task Scheduler argument (CommandLineToArgvW rules).</summary>
    public static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') result.Append('\\', slashes * 2 + 1).Append('"'); else result.Append('\\', slashes).Append(ch);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static bool ValidSid(string sid) => sid is { Length: > 8 and < 200 } && sid.StartsWith("S-1-", StringComparison.Ordinal) && sid.All(c => char.IsAsciiDigit(c) || c is '-' or 'S');
    private static void Element(XmlWriter xml, string name, string value) { xml.WriteStartElement(name, Namespace); xml.WriteString(value); xml.WriteEndElement(); }
}
