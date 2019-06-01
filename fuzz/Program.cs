using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace fuzz
{
   class MainClass
   {
      /// <summary>
      ///   Yields values form each enumerable in sequence, moving from
      ///   one to the next as each is exhausted.
      /// </summary>
      private static IEnumerable<T> Chain<T>(IEnumerable<T>[] enumerables)
      {
         foreach (var enumerable in enumerables)
         {
            foreach (var value in enumerable)
            {
               yield return value;
            }
         }
      }

      /// <summary>
      ///   Recursively explores a directory, and adds all files found within
      ///   to the files set.
      /// </summary>
      private static IEnumerable<string> Traverse(string path)
      {
         var empty = new string[0];
         IEnumerable<string> files = empty;

         try
         {
            files = Directory.EnumerateFiles(path);
         }
         catch (UnauthorizedAccessException)
         {
         }

         foreach (string filename in files )
         {
            yield return filename;
         }

         IEnumerable<string> directories = empty;
         try
         {
            directories = Directory.EnumerateDirectories(path);
         }
         catch (UnauthorizedAccessException)
         {
         }

         foreach (string directory in directories)
         {
            string relative_directory = Path.GetFileName(directory);
            if (relative_directory == "." || relative_directory == "..")
            {
              continue;
            }

            foreach (var filename in Traverse(directory))
            {
              yield return filename;
            }
         }
      }

      class FuzzyMatchState
      {
         public int Index { get; private set; }
         public int Score { get; private set; }

         public FuzzyMatchState(int index, int score)
         {
            Index = index;
            Score = score;
         }

         public void Penalize()
         {
            Score++;
         }

         public bool Started()
         {
            return Index > 0;
         }

         public bool IsDone(string pattern)
         {
            return Index == pattern.Length;
         }

         public bool Matches(char input, string pattern)
         {
            return pattern[Index] == input;
         }

         public FuzzyMatchState NextState()
         {
            return new FuzzyMatchState(Index + 1, Score);
         }
      }

      /// <summary>
      ///   Scores how well a string is a fuzzy match for the pattern,
      ///   with lower scores being better.
      /// </summary>
      private static int? FuzzyMatch(string subject, string pattern)
      {
         // The reason we do it this way is because we want to get the best
         // score possible. For example, if we had the pattern "system" and the
         // input "sys_system", we want to choose the "system" as the fuzzy
         // match and not "sys____tem" (with _ as wildcards)
         //
         // In order to do that, we examine each possibility when there's a
         // match (either take it, or don't) and see which end state has the
         // best outcome
         var states = new List<FuzzyMatchState>();
         var completedStates = new List<FuzzyMatchState>();
         states.Add(new FuzzyMatchState(0, 0));

         foreach (var ch in subject)
         {
            var nextStates = new List<FuzzyMatchState>();
            foreach (var state in states)
            {
               if (state.IsDone(pattern))
               {
                  completedStates.Add(state);
               }
               else if (state.Matches(ch, pattern))
               {
                  var nextState = state.NextState();
                  nextStates.Add(state);

                  if (nextState.IsDone(pattern))
                  {
                     completedStates.Add(nextState);
                  }
                  else
                  {
                     nextStates.Add(nextState);
                  }
               }
               else
               {
                  // We only want the intervening characters to count, not
                  // anything that comes before the pattern entirely
                  if (state.Started())
                  {
                     state.Penalize();
                  }

                  nextStates.Add(state);
               }
            }

            states = nextStates;
         }

         if (completedStates.Count == 0)
         {
            return null;
         }
         else
         {
            return completedStates.Min(state => state.Score);
         }
      }

      /// <summary>
      ///   Performs a fuzzy match over a filesystem path, by matching each
      ///   path component separately and only accepting a filename if all
      ///   components from the pattern match.
      /// </summary>
      private static int? FuzzyMatchPath(string fuzzypath, string filename, bool exact)
      {
         string[] fuzzycomponents = fuzzypath.Split('/', '\\');
         string[] filenamecomponents = filename.Split('/', '\\');

         // Nobody would expect that tmp/a/b/c could be a match for /tmp/c
         if (fuzzycomponents.Length > filenamecomponents.Length)
            return null;

         int score = 0;
         for (int i = 1; i <= fuzzycomponents.Length; i++)
         {
            string fuzzycomponent = fuzzycomponents[fuzzycomponents.Length - i];
            string filenamecomponent = filenamecomponents[filenamecomponents.Length - i];

            // All this path component is used to signify is the level in the file hierarchy,
            // so we don't care what the actual contents are
            if (fuzzycomponent.Length == 0)
               continue;

            // None of these make sense as patterns, since they either allow
            // anything or nothing. Treat these as if they were exact matches.
            bool useExactMatch = exact ||
               fuzzycomponent == "^" ||
               fuzzycomponent == "$" ||
               fuzzycomponent == "^$";

            bool startAnchor = !useExactMatch && fuzzycomponent.StartsWith("^");
            bool endAnchor = !useExactMatch && fuzzycomponent.EndsWith("$");

            if (startAnchor && endAnchor)
            {
               string match = fuzzycomponent.Substring(1, fuzzycomponent.Length - 2);
               if (filenamecomponent != match)
               {
                  return null;
               }
            }
            else if (startAnchor)
            {
               string lead = fuzzycomponent.Substring(1);
               if (!filenamecomponent.StartsWith(lead))
               {
                  return null;
               }
            }
            else if (endAnchor)
            {
               string tail = fuzzycomponent.Substring(0, fuzzycomponent.Length - 1);
               if (!filenamecomponent.EndsWith(tail))
               {
                  return null;
               }
            }
            else
            {
               int? componentScore = FuzzyMatch(filenamecomponent, fuzzycomponent);
               if (componentScore.HasValue)
               {
                 score += componentScore.Value;
               }
               else
               {
                 return null;
               }
            }
         }

         return score;
      }

      struct Args
      {
         public string Pattern;
         public List<string> Paths;
         public int Limit;
         public bool Exact;
      }

      private static Args ParseArgs(string[] args)
      {
         var result = new Args();
         result.Limit = -1;
         result.Pattern = null;
         result.Paths = new List<string>();
         result.Exact = false;

         int i = 0;
         while (i < args.Length)
         {
            if (args[i] == "--")
            {
               i++;
               break;
            }
            else if (args[i] == "-l" || args[i] == "--limit")
            {
               if (result.Limit != -1)
               {
                  throw new ArgumentException("Cannot provide multiple limits");
               }

               i++;
               if (i >= args.Length)
               {
                  throw new ArgumentException("-l / --limit must be followed by a number");
               }

               try
               {
                  result.Limit = int.Parse(args[i]);
                  if (result.Limit <= 0) throw new ArgumentException("The limit must be positive");
               }
               catch (FormatException)
               {
                  throw new ArgumentException(args[i] + " is not a valid limit");
               }
               i++;
            }
            else if (args[i] == "-x" || args[i] == "--exact")
            {
               i++;
               if (result.Exact)
               {
                  throw new ArgumentException("Cannot provide duplicate exact flags");
               }

               result.Exact = true;
            }
            else
            {
               break;
            }
         }

         if (i >= args.Length)
         {
            throw new ArgumentException("A pattern is required");
         }

         result.Pattern = args[i].ToLower();
         i++;

         while (i < args.Length)
         {
            result.Paths.Add(args[i]);
            i++;
         }

         if (result.Limit == -1)
         {
            result.Limit = 25;
         }

         if (result.Paths.Count == 0)
         {
            result.Paths.Add(".");
         }

         return result;
      }

      public static void Main(string[] args)
      {
         Args settings;
         try
         {
            settings = ParseArgs(args);
         }
         catch (ArgumentException error)
         {
            Console.Error.WriteLine("fuzz.exe [-l|--limit LIMIT] [-x|--exact] [--] PATTERN [DIR...]");
            Console.Error.WriteLine(error.Message);
            return;
         }

         var traversers = new IEnumerable<string>[settings.Paths.Count];
         for (int i = 0; i < settings.Paths.Count; i++)
         {
            traversers[i] = Traverse(settings.Paths[i]);
         }

         var scores = new Dictionary<string, int>();
         foreach (string filename in Chain(traversers).Distinct())
         {
            int? score = FuzzyMatchPath(settings.Pattern, filename.ToLower(), settings.Exact);
            if (score != null)
            {
               scores.Add(filename, score.Value);
            }
         }

         var best_matches = scores.Keys
            .OrderBy(filename => scores[filename])
            .Take(settings.Limit);

         foreach (string match in best_matches)
         {
            Console.WriteLine(match);
         }
      }
   }
}
