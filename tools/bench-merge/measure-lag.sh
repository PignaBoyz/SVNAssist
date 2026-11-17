cd "$(dirname "$0")/wcL"
U=svn://127.0.0.1:3691/branches/feat
N=${1:-40}
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
svn revert -R -q .
t0=$(ms)
svn merge --accept postpone -c 4-$LAST $U >/dev/null
t1=$(ms)
echo "C one range 4-$LAST: $((t1-t0)) ms"
svn revert -R -q .
