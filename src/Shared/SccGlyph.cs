// Mappa lo stato SVN (nome di SvnStatusKind) sul glifo mostrato in Solution Explorer. Logica pura: la usano Host e test.
namespace SVNAssist.Scc
{
    public enum SccGlyph { None, Normal, Modified, Added, Unversioned, Conflict, Deleted }

    public static class SccGlyphMap
    {
        /// <summary>Stato SVN -> glifo; null/ignorato/esterno/sconosciuto = nessun glifo.</summary>
        public static SccGlyph For(string status)
        {
            switch (status)
            {
                case "Normal": return SccGlyph.Normal;
                case "Modified": case "Replaced": return SccGlyph.Modified;
                case "Added": return SccGlyph.Added;
                case "Deleted": case "Missing": return SccGlyph.Deleted;
                case "Conflicted": case "Obstructed": case "Incomplete": return SccGlyph.Conflict;
                case "Unversioned": return SccGlyph.Unversioned;
                default: return SccGlyph.None;
            }
        }
    }
}
