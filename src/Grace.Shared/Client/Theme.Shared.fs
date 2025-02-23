namespace Grace.Shared.Client

open System
open System.Drawing
open System.Collections.Generic

// A few comments on Theme:
// It's "optimized" for being able to add #RRGGBB values to the configuration easily.

module Theme =

    module DisplayColor =
        let Added = "added"
        let Changed = "changed"
        let Deemphasized = "deemphasized"
        let Deleted = "deleted"
        let Error = "error"
        let Highlighted = "highlighted"
        let Important = "important"
        let Verbose = "verbose"

    let format (color: Color) = $"#{color.R:X2}{color.G:X2}{color.B:X2}"

    type Theme =
        { Name: string
          DisplayColorOptions: IReadOnlyDictionary<string, string> }

        override this.ToString() = $"{this.Name}"

        static member Create (name: string) (colors: Color[]) =
            let displayColorOptions = Dictionary<string, string>()
            displayColorOptions.Add(DisplayColor.Added, format colors[0])
            displayColorOptions.Add(DisplayColor.Changed, format colors[1])
            displayColorOptions.Add(DisplayColor.Deemphasized, format colors[2])
            displayColorOptions.Add(DisplayColor.Deleted, format colors[3])
            displayColorOptions.Add(DisplayColor.Error, format colors[4])
            displayColorOptions.Add(DisplayColor.Highlighted, format colors[5])
            displayColorOptions.Add(DisplayColor.Important, format colors[6])
            displayColorOptions.Add(DisplayColor.Verbose, format colors[7])

            { Name = name; DisplayColorOptions = displayColorOptions }

    let private defaultColors =
        [| Color.FromArgb(0x00, 0xaf, 0x5f)
           Color.Purple
           Color.Gray
           Color.DarkRed
           Color.Red
           Color.White
           Color.FromArgb(0xe5, 0xc0, 0x7b)
           Color.Magenta |]

    /// Default color theme, with green for adds, red for deletes, etc.
    let DefaultTheme = Theme.Create "Default" defaultColors
