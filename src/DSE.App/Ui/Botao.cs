using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DSE.App.Ui;

/// <summary>
/// Botão da casa (padrão interface-simples): canto redondo, realce do mouse,
/// <see cref="Selecionado"/> = laranja com frente branca. Visual no estilo
/// implícito de App.xaml.
/// </summary>
public class Botao : Button
{
    public static readonly DependencyProperty SelecionadoProperty = DependencyProperty.Register(
        nameof(Selecionado), typeof(bool), typeof(Botao), new PropertyMetadata(false));

    public static readonly DependencyProperty CorProperty = DependencyProperty.Register(
        nameof(Cor), typeof(Brush), typeof(Botao), new PropertyMetadata(null));

    public static readonly DependencyProperty RaioProperty = DependencyProperty.Register(
        nameof(Raio), typeof(CornerRadius), typeof(Botao), new PropertyMetadata(new CornerRadius(10)));

    public bool Selecionado
    {
        get => (bool)GetValue(SelecionadoProperty);
        set => SetValue(SelecionadoProperty, value);
    }

    public Brush? Cor
    {
        get => (Brush?)GetValue(CorProperty);
        set => SetValue(CorProperty, value);
    }

    public CornerRadius Raio
    {
        get => (CornerRadius)GetValue(RaioProperty);
        set => SetValue(RaioProperty, value);
    }

    /// <summary>
    /// Monta o conteúdo: ícone (opcional) + texto. Lista suspensa
    /// (<c>seta_baixo</c>) põe a seta DEPOIS do texto, como no padrão.
    /// </summary>
    public static Botao Criar(string? texto, string? icone = null, double largura = double.NaN,
        double altura = 32, double raio = 8, double tam = 14, bool semi = true, double ladoIcone = 16,
        string? dica = null, Brush? cor = null)
    {
        var b = new Botao
        {
            Width = largura,
            Height = altura,
            Raio = new CornerRadius(raio),
            FontSize = tam,
            Padding = new Thickness(8, 0, 8, 0),
        };
        if (!semi) b.FontFamily = (FontFamily)Application.Current.FindResource("Letra");
        if (cor != null) b.Cor = cor;
        if (dica != null) b.ToolTip = dica;
        b.DefinirConteudo(texto, icone, ladoIcone);
        return b;
    }

    public void DefinirConteudo(string? texto, string? icone, double ladoIcone = 16)
    {
        var linha = new StackPanel { Orientation = Orientation.Horizontal };
        bool temTexto = !string.IsNullOrEmpty(texto);
        UIElement? ico = icone == null ? null : Icones.Criar(icone, ladoIcone);
        var txt = temTexto
            ? new TextBlock { Text = texto, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
            : null;

        if (ico != null && icone == "seta_baixo")
        {
            if (txt != null) linha.Children.Add(txt);
            ((FrameworkElement)ico).Margin = new Thickness(temTexto ? 8 : 0, 0, 0, 0);
            linha.Children.Add(ico);
        }
        else
        {
            if (ico != null) linha.Children.Add(ico);
            if (txt != null)
            {
                txt.Margin = new Thickness(ico != null ? 8 : 0, 0, 0, 0);
                linha.Children.Add(txt);
            }
        }
        Content = linha;
    }
}
