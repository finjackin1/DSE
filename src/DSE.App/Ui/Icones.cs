using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DSE.App.Ui;

/// <summary>
/// Ícones do sistema da casa: grade 24, traço 2 com ponta redonda (o mesmo
/// desenho do icones.py do padrão). Sem cor = herda o Foreground de quem
/// contém (botão selecionado fica branco, desabilitado fica apagado).
/// </summary>
public static class Icones
{
    // Traços (linhas abertas). Coordenadas na grade 24.
    private static readonly Dictionary<string, string> Tracos = new()
    {
        ["fechar"] = "M6,6 L18,18 M18,6 L6,18",
        ["minimizar"] = "M6,12 L18,12",
        ["opcoes"] = "M4,6 L20,6 M4,12 L20,12 M4,18 L20,18",
        ["voltar"] = "M19,12 L5,12 M11,6 L5,12 L11,18",
        ["mais"] = "M12,5 L12,19 M5,12 L19,12",
        ["menos"] = "M5,12 L19,12",
        ["lixo"] = "M4.5,7 L19.5,7 M9.5,7 L10,4.5 L14,4.5 L14.5,7 M6.5,7 L7.5,19.5 L16.5,19.5 L17.5,7",
        ["desligar"] = "M7.3,6.4 A7.5,7.5 0 1 0 16.7,6.4 M12,3.5 L12,11",
        ["seta_baixo"] = "M7,10 L12,15 L17,10",
        ["baixar"] = "M12,4 L12,15 M7,10.5 L12,15.5 L17,10.5 M5,20 L19,20",
        ["ajuda"] = "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 M9.6,9.6 A2.5,2.5 0 1 1 13.2,11.9 C12.4,12.4 12,12.9 12,13.8",
        ["bluetooth"] = "M7,7.5 L17,16.5 L12,21 L12,3 L17,7.5 L7,16.5",
        ["controle"] = "M7.5,7 L16.5,7 A4.5,4.5 0 0 1 20.9,10.6 L21.6,15.2 A2.7,2.7 0 0 1 16.9,17.4 L15.4,15.5 L8.6,15.5 L7.1,17.4 A2.7,2.7 0 0 1 2.4,15.2 L3.1,10.6 A4.5,4.5 0 0 1 7.5,7 Z M7.5,9.8 L7.5,12.8 M6,11.3 L9,11.3",
        ["luz"] = "M9,18 L15,18 M10,21 L14,21 M12,3 A6,6 0 0 1 15.5,13.9 L15,15.5 L9,15.5 L8.5,13.9 A6,6 0 0 1 12,3 Z",
    };

    // Partes cheias (bolinhas dos trilhos, ponto do "?", botões do controle).
    private static readonly Dictionary<string, (double x, double y, double r)[]> Bolas = new()
    {
        ["opcoes"] = new[] { (15.0, 6.0, 2.6), (8.0, 12.0, 2.6), (13.0, 18.0, 2.6) },
        ["ajuda"] = new[] { (12.0, 17.0, 1.25) },
        ["controle"] = new[] { (15.6, 10.6, 1.15), (17.8, 12.6, 1.15) },
    };

    /// <summary>Ícone de <paramref name="lado"/> px (já na escala da janela). cor null = herda.</summary>
    public static FrameworkElement Criar(string nome, double lado, Brush? cor = null)
    {
        var tela = new Canvas { Width = 24, Height = 24 };
        if (Tracos.TryGetValue(nome, out var dados))
        {
            var p = new Path
            {
                Data = Geometry.Parse(dados),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            Pintar(p, Shape.StrokeProperty, cor);
            tela.Children.Add(p);
        }
        if (Bolas.TryGetValue(nome, out var bolas))
        {
            foreach (var (x, y, r) in bolas)
            {
                var e = new Ellipse { Width = r * 2, Height = r * 2 };
                Canvas.SetLeft(e, x - r);
                Canvas.SetTop(e, y - r);
                Pintar(e, Shape.FillProperty, cor);
                tela.Children.Add(e);
            }
        }
        return new Viewbox
        {
            Width = lado,
            Height = lado,
            Child = tela,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = false,
        };
    }

    private static void Pintar(Shape s, DependencyProperty prop, Brush? cor)
    {
        if (cor != null)
        {
            s.SetValue(prop, cor);
            return;
        }
        s.SetBinding(prop, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), RelativeSource = RelativeSource.Self });
    }
}
