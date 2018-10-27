using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace fuzz
{
   class MainClass
   {
      /// <summary>
      ///   Recursively explores a directory, and adds all files found within
      ///   to the files set.
      /// </summary>
      private static void Traverse(string path, ISet<string> files)
      {
         foreach (string filename in Directory.EnumerateFiles(path))
         {
            files.Add(filename);
         }

         try
         {
            foreach (string directory in Directory.EnumerateDirectories(path))
            {
               string relative_directory = Path.GetFileName(directory);
               if (relative_directory == "." || relative_directory == "..")
               {
                  continue;
               }

               Traverse(directory, files);
            }
         }
         catch (UnauthorizedAccessException)
         {
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

      private static int? FuzzyMatchPath(string fuzzypath, string filename)
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

         return score;
      }

      public static void Usage(string message)
      {
         Console.Error.WriteLine(message);
         Console.Error.WriteLine("fuzz [-l/--limit <limit>] [--] <pattern> [dir...]");
         Environment.Exit(1);
      }

      struct Args
      {
         public string Pattern;
         public List<string> Paths;
         public int Limit;
      }

      private static Args ParseArgs(string[] args)
      {
         var result = new Args();
         result.Limit = -1;
         result.Pattern = null;
         result.Paths = new List<string>();

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
               if (i > args.Length)
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
            Console.Error.WriteLine("fuzz.exe [-l|--limit LIMIT] [--] PATTERN [DIR...]");
            Console.Error.WriteLine(error.Message);
            return;
         }

         var filenames = new HashSet<string>();
         foreach (var path in settings.Paths)
         {
            Traverse(path, filenames);
         }

         var to_remove = new Stack<string>();
         var scores = new Dictionary<string, int>();
         foreach (string filename in filenames)
         {
            int? score = FuzzyMatchPath(settings.Pattern, filename.ToLower());
            if (score != null)
            {
               scores.Add(filename, score.Value);
            }
            else
            {
               to_remove.Push(filename);
            }
         }

         foreach (string invalid in to_remove)
         {
            filenames.Remove(invalid);
         }

         var best_matches =
            filenames.OrderBy(sortpath => scores[sortpath])
            .Take(settings.Limit);

         foreach (string match in best_matches)
         {
            Console.WriteLine(match);
         }
      }
   }
}
