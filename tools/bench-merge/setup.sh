set -e
cd "$(dirname "$0")"
rm -rf repo wc wcb
svnadmin create repo
U=file://$PWD/repo
svn mkdir -q -m init $U/trunk $U/branches
svn co -q $U/trunk wc
cd wc
for d in $(seq 1 100); do mkdir -p src/d$d; for f in $(seq 1 30); do printf 'line1\nline2\nline3\nline4\nline5\n' > src/d$d/f$f.txt; done; done
svn add -q src
svn ci -q -m "base 3000 files"
cd ..
svn cp -q -m "branch" $U/trunk $U/branches/feat
svn co -q $U/branches/feat wcb
cd wcb
for n in $(seq 1 420); do d=$(( (n % 100) + 1 )); f=$(( (n % 30) + 1 )); echo "change $n" >> src/d$d/f$f.txt; svn ci -q -m "rev edit $n" ; done
