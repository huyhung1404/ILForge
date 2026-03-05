using System.IO;
using UnityEditor;

namespace ILForge.Editor
{
    [FilePath("ProjectSettings/IL_WeaverSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class WeaverSettings : ScriptableSingleton<WeaverSettings>
    {
        public bool Enabled;
        public string[] Assemblies = new[] { "Assembly-CSharp" };

        private const string k_configPath = "ProjectSettings/ILForge_CompilerSettings.txt";

        public void SaveData()
        {
            Save(true);
            using (var writer = new StreamWriter(k_configPath, false))
            {
                writer.WriteLine(Enabled.ToString());
                foreach (var asm in Assemblies)
                {
                    var cleanName = asm.Trim();
                    if (!string.IsNullOrEmpty(cleanName))
                    {
                        writer.WriteLine(cleanName);
                    }
                }
            }
        }
    }
}