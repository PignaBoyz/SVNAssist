using System.Collections.Generic;

namespace SVNAssist.Navigation
{
    /// <summary>Navigazione tra blocchi modificati (logica pura, condivisa host/test): sceglie la riga di destinazione.</summary>
    public static class DiffNav
    {
        /// <summary>Prima riga di blocco dopo <paramref name="current"/>; oltre l'ultimo, giro al primo. 0 se non ci sono blocchi.</summary>
        public static int Next(IReadOnlyList<int> starts, int current)
        {
            if (starts == null || starts.Count == 0) return 0;
            int best = 0, first = int.MaxValue;
            foreach (var s in starts)
            {
                if (s < first) first = s;
                if (s > current && (best == 0 || s < best)) best = s;
            }
            return best != 0 ? best : first;
        }

        /// <summary>Ultima riga di blocco prima di <paramref name="current"/>; prima del primo, giro all'ultimo. 0 se non ci sono blocchi.</summary>
        public static int Previous(IReadOnlyList<int> starts, int current)
        {
            if (starts == null || starts.Count == 0) return 0;
            int best = 0, last = 0;
            foreach (var s in starts)
            {
                if (s > last) last = s;
                if (s < current && s > best) best = s;
            }
            return best != 0 ? best : last;
        }
    }
}
