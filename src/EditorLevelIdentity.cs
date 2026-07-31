using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace Kiner.ADOFAIAudioSync
{
    internal static class EditorLevelIdentity
    {
        internal static string Resolve(scnEditor editor)
        {
            if (editor == null || editor.levelData == null || editor.floors == null)
                return string.Empty;

            string path = string.Empty;
            try
            {
                if (editor.customLevel != null)
                    path = editor.customLevel.levelPath ?? string.Empty;
                if (string.IsNullOrEmpty(path) && scnGame.instance != null)
                    path = scnGame.instance.levelPath ?? string.Empty;
                if (!string.IsNullOrEmpty(path))
                    path = Path.GetFullPath(path).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
            }
            catch
            {
                // The raw path is still useful when it is a URL or otherwise non-filesystem.
            }

            return path.ToUpperInvariant() + "|" +
                   RuntimeHelpers.GetHashCode(editor.levelData) + "|" +
                   editor.floors.Count + "|" +
                   editor.levelData.bpm.ToString("R", CultureInfo.InvariantCulture) + "|" +
                   editor.GetInstanceID();
        }
    }
}
