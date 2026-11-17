cd "$(dirname "$0")/wc"
U=file://$(dirname "$PWD")/repo/branches/feat
N=${1:-100}
LAST=$((3 + N))
ms() { date +%s%3N; }
svn revert -R -q .
t0=$(ms)
for r in $(seq 4 $LAST); do svn merge --accept postpone -c $r $U >/dev/null; done
t1=$(ms)
echo "A per-revision ($N procs): $((t1-t0)) ms  => $(( (t1-t0)/N )) ms/rev"
svn revert -R -q .
LIST=$(seq -s, 4 $LAST)
t0=$(ms)
svn merge --accept postpone -c $LIST $U >/dev/null
t1=$(ms)
echo "B one process, list of $N separate ranges: $((t1-t0)) ms => $(( (t1-t0)/N )) ms/rev"
svn merge --accept postpone -c $LIST $U | head -3
svn revert -R -q .
t0=$(ms)
svn merge --accept postpone -c 4-$LAST $U >/dev/null
t1=$(ms)
echo "C one range 4-$LAST: $((t1-t0)) ms"
svn revert -R -q .
t0=$(ms)
svn merge --dry-run --accept postpone -c 4-$LAST $U >/dev/null
t1=$(ms)
echo "D dry-run range: $((t1-t0)) ms"
t0=$(ms)
svn mergeinfo --show-revs eligible $U >/dev/null
t1=$(ms)
echo "E mergeinfo eligible: $((t1-t0)) ms"
t0=$(ms)
svn status >/dev/null
t1=$(ms)
echo "F svn status: $((t1-t0)) ms"
