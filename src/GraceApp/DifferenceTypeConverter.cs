using Microsoft.FSharp.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Grace.Shared.Types;
using static Grace.Shared.Utilities;

namespace GraceApp
{
    public class DifferenceTypeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return discriminatedUnionCaseNameToString((DifferenceType)value);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException("DifferenceTypeConverter does not implement ConvertBack (yet).");
        }
    }
}
