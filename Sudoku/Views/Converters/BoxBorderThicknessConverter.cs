using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Sudoku.Views.Converters;

/// <summary>マスの行・列番号から、3×3ブロックの境界線を太く見せるための枠線の太さを計算する。</summary>
public class BoxBorderThicknessConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        int row = (int)values[0];
        int col = (int)values[1];

        double left = col % 3 == 0 ? 2 : 0.5;
        double top = row % 3 == 0 ? 2 : 0.5;
        double right = col == 8 ? 2 : 0;
        double bottom = row == 8 ? 2 : 0;

        return new Thickness(left, top, right, bottom);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}