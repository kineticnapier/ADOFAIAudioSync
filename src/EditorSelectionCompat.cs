using System;
using System.Reflection;

namespace Kiner.ADOFAIAudioSync
{
    /// <summary>
    /// Reads the editor's active floor without depending on a field that is absent from
    /// some ADOFAI builds. selectedFloors is the stable path; reflection is only a
    /// compatibility fallback for modified editor assemblies.
    /// </summary>
    internal static class EditorSelectionCompat
    {
        internal static int ResolveSelectedFloor(scnEditor editor, int fallback)
        {
            if (editor == null) return fallback;

            try
            {
                if (editor.selectedFloors != null && editor.selectedFloors.Count > 0)
                {
                    // The most recently selected floor is normally appended last.
                    for (int i = editor.selectedFloors.Count - 1; i >= 0; i--)
                    {
                        scrFloor floor = editor.selectedFloors[i];
                        if (floor != null) return Math.Max(0, floor.seqID);
                    }
                }
            }
            catch
            {
                // Continue to reflection fallbacks.
            }

            object value = ReadMember(editor, "lastSelectedFloor") ??
                           ReadMember(editor, "selectedFloor") ??
                           ReadMember(editor, "currentFloor");
            scrFloor reflectedFloor = value as scrFloor;
            return reflectedFloor == null ? fallback : Math.Max(0, reflectedFloor.seqID);
        }

        private static object ReadMember(object target, string name)
        {
            if (target == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = target.GetType();
            try
            {
                FieldInfo field = type.GetField(name, flags);
                if (field != null) return field.GetValue(target);

                System.Reflection.PropertyInfo property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(target, null);
            }
            catch
            {
                // A compatibility probe must never break editor playback.
            }
            return null;
        }
    }
}
