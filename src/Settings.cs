using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace NetPulse
{
    public sealed class Settings
    {
        public double Left = Double.NaN, Top = Double.NaN;
        public bool Compact, Bits;
        public bool AlwaysOnTop = true;
        public string AdapterId = "";

        public static Settings Load(string path)
        {
            try
            {
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    return (Settings)new XmlSerializer(typeof(Settings)).Deserialize(reader) ?? new Settings();
            }
            catch (IOException) { return new Settings(); }
            catch (UnauthorizedAccessException) { return new Settings(); }
            catch (InvalidOperationException) { return new Settings(); }
            catch (XmlException) { return new Settings(); }
        }

        public bool Save(string path)
        {
            try
            {
                using (var file = File.Create(path + ".tmp")) new XmlSerializer(typeof(Settings)).Serialize(file, this);
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
                else File.Move(path + ".tmp", path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
