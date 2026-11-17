cd "$(dirname "$0")/wcL"
U=svn://127.0.0.1:3691/branches/feat
ms() { date +%s%3N; }
for N in 1 5 10 25 100 420; do
  LAST=$((3 + N))
  svn revert -R -q .
  t0=$(ms)
  svn merge --accept postpone -c 4-$LAST $U >/dev/null
  t1=$(ms)
  echo "range of $N revs: $((t1-t0)) ms"
done
svn revert -R -q .
t0=$(ms)
svn merge --dry-run --accept postpone -c 4-423 $U >/dev/null
t1=$(ms)
echo "dry-run range of 420: $((t1-t0)) ms"
