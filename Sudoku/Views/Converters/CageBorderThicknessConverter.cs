using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Sudoku.Views.Converters;

///<summary>4方向（左・上・右・下）の「ケージ境界かどうか」フラグから、
///ケージ枠線描画用のBorderThicknessを計算する。</summary>
public class CageBorderThicknessConverter : IMultiValueConverter
{
    private const double BorderSize = 1.6;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool left = values.Length > 0 && values[0] is true;
        bool top = values.Length > 1 && values[1] is true;
        bool right = values.Length > 2 && values[2] is true;
        bool bottom = values.Length > 3 && values[3] is true;

        return new Thickness(
            left ? BorderSize : 0,
            top ? BorderSize : 0,
            right ? BorderSize : 0,
            bottom ? BorderSize : 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}