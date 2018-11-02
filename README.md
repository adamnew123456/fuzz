# What is this?

This is a basic filesystem fuzzy searcher:

```sh
# Searches in the working directory
$ fuzz.exe my_pattern

# Extra directories can be included 
$ fuzz.exe my_pattern /home/me/Documents /home/me/Desktop

# The pattern can be a partial path - in order for a filename
# to be match, each pattern component must match. The search
# starts from the end of the filename.
$ fuzz.exe .git/head /home/me/repos

# The pattern can include ^ and $ anchors, to test for startswit
# and endswith
$ fuzz.exe '.git/HEAD$' /home/me/repos

# The pattern can also include both, to allow only exact matches
$ fuzz.exe '.git/^HEAD$' /home/me/repos

# You can include empty paths, to force a certain number of parent
# directories, even if you don't care what they are
$ fuzz.exe //head /home/me/repos
```

# Building

Run `xbuild` or `msbuild` to compile the solution.

# Running

    fuzz.exe [-l|--limit LIMIT] [-x|--exact] [--] PATTERN [DIR...]
    
If the limit is not provided, the default prints the top 25 matches.

If exact is provided, then `^` and `$` are not treated as special characters,
and the matches are always done in fuzzy mode.
