using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DSE.Core.Controllers;

namespace DSE.App.Views;

/// <summary>
/// Monta o ícone do controle a partir das imagens processadas (silhueta
/// branca, fundo transparente, desenho ORIGINAL intacto — nada é recortado
/// do corpo). O touchpad é recolorido usando uma máscara de opacidade
/// extraída do mesmo desenho, então a forma exata (incluindo a borda
/// original) fica preservada — só a cor do preenchimento muda. Clicar no
/// touchpad troca o modo do LED; clicar no resto do corpo liga/desliga a
/// emulação desse controle.
/// </summary>
public static class ControllerIconFactory
{
    private sealed record Spec(
        string BodyAssetPath, string MaskAssetPath,
        double ImageWidth, double ImageHeight,
        Rect TouchpadBox,
        double TouchMaxX, double TouchMaxY);

    // Máscaras extraídas do buraco transparente do touchpad nas imagens
    // editadas pelo usuário (ds4e.png / dse.png) — formato e posição exatos
    // que ele desenhou. Corpo com as áreas brancas preservadas e fundo
    // externo removido pelo próprio usuário.
    private static readonly Dictionary<PhysicalControllerType, Spec> Specs = new()
    {
        [PhysicalControllerType.DualShock4] = new Spec(
            "/Assets/ds4_body.png", "/Assets/ds4_touchpad_mask.png",
            900, 567, new Rect(309, 29, 279, 150),
            // Grade que o touchpad do DS4 reporta. Se o ponto não alcançar as
            // beiradas (ou passar delas), é AQUI que se calibra.
            TouchMaxX: 1920, TouchMaxY: 942),
        [PhysicalControllerType.DualSense] = new Spec(
            "/Assets/dualsense_body.png", "/Assets/dualsense_touchpad_mask.png",
            900, 590, new Rect(292, 15, 317, 177),
            TouchMaxX: 1920, TouchMaxY: 1080),
    };

    public static FrameworkElement Build(
        PhysicalControllerType type,
        Brush touchpadColor,
        double boxWidth,
        double boxHeight,
        Action onBodyClick,
        Action onTouchpadClick,
        out System.Windows.Shapes.Rectangle touchpadRect,
        out System.Windows.Shapes.Ellipse touchDot)
    {
        var spec = Specs[type];

        var canvas = new Canvas
        {
            Width = spec.ImageWidth,
            Height = spec.ImageHeight,
            Background = Brushes.Transparent
        };

        // Corpo do controle — desenho ORIGINAL intacto, nada recortado.
        var bodyImage = new Image
        {
            Source = new BitmapImage(new Uri($"pack://application:,,,/DSE.App;component{spec.BodyAssetPath}")),
            Width = spec.ImageWidth,
            Height = spec.ImageHeight,
            Stretch = Stretch.Fill,
            Cursor = Cursors.Hand
        };
        RenderOptions.SetBitmapScalingMode(bodyImage, BitmapScalingMode.HighQuality);
        bodyImage.MouseLeftButtonUp += (_, _) => onBodyClick();
        canvas.Children.Add(bodyImage);

        // Touchpad recolorível: um retângulo da cor desejada, mascarado
        // pela forma EXATA do touchpad (extraída do mesmo desenho) — a
        // borda/contorno original continua visível, só o preenchimento
        // muda de cor.
        var maskBrush = new ImageBrush(
            new BitmapImage(new Uri($"pack://application:,,,/DSE.App;component{spec.MaskAssetPath}")))
        {
            Stretch = Stretch.Fill
        };
        RenderOptions.SetBitmapScalingMode(maskBrush, BitmapScalingMode.HighQuality);

        var touchpad = new System.Windows.Shapes.Rectangle
        {
            Width = spec.TouchpadBox.Width,
            Height = spec.TouchpadBox.Height,
            Fill = touchpadColor,
            OpacityMask = maskBrush,
            Cursor = Cursors.Hand,
            ToolTip = "Clique pra trocar o modo do LED"
        };
        Canvas.SetLeft(touchpad, spec.TouchpadBox.X);
        Canvas.SetTop(touchpad, spec.TouchpadBox.Y);
        touchpad.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onTouchpadClick();
        };
        canvas.Children.Add(touchpad);
        touchpadRect = touchpad;

        // Ponto do toque: fica ACIMA do touchpad no canvas e é posicionado em
        // coordenadas da imagem, então acompanha a escala do card sozinho.
        // Nasce escondido; quem o move é o relógio da janela.
        touchDot = new System.Windows.Shapes.Ellipse
        {
            Width = 26,
            Height = 26,
            Fill = new SolidColorBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF)),
            Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false // não pode roubar o clique do touchpad
        };
        canvas.Children.Add(touchDot);

        canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (!e.Handled) onBodyClick();
        };

        return new Viewbox
        {
            Width = boxWidth,
            Height = boxHeight,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            Child = canvas
        };
    }

    /// <summary>
    /// Coloca o ponto onde o dedo está. As coordenadas do controle vêm numa
    /// grade própria (ver TouchMaxX/Y), e aqui viram posição dentro da caixa
    /// do touchpad na imagem — regra de três simples, com o ponto centrado no
    /// dedo em vez de ancorado pelo canto.
    /// </summary>
    public static void PosicionarToque(
        System.Windows.Shapes.Ellipse dot, PhysicalControllerType type,
        bool ativo, int x, int y)
    {
        if (!ativo)
        {
            dot.Visibility = Visibility.Collapsed;
            return;
        }

        var box = Specs[type].TouchpadBox;
        var spec = Specs[type];

        double px = box.X + Math.Clamp(x / spec.TouchMaxX, 0, 1) * box.Width;
        double py = box.Y + Math.Clamp(y / spec.TouchMaxY, 0, 1) * box.Height;

        Canvas.SetLeft(dot, px - dot.Width / 2);
        Canvas.SetTop(dot, py - dot.Height / 2);
        dot.Visibility = Visibility.Visible;
    }
}
