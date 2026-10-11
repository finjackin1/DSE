using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;
using DSE.Core.Diagnostics;
using DSE.Core.Setup;

namespace DSE.App.Ui;

/// <summary>
/// Atalhos globais do teclado por RegisterHotKey (sem gancho de teclado),
/// numa janela só de mensagens: funcionam com o DSE na bandeja. Nascem vazios;
/// quem define é ele, na tela de Opções.
/// </summary>
public sealed class AtalhosGlobais : IDisposable
{
    public const int ModAlt = 1, ModCtrl = 2, ModShift = 4, ModWin = 8;
    private const int ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;

    /// <summary>Ações na ordem em que aparecem no menu "+".</summary>
    public static readonly (string Acao, string Rotulo)[] Acoes =
    {
        ("janela", "Mostrar / esconder a janela"),
        ("perfil", "Alternar perfil (Xbox ↔ DS4)"),
        ("emulacao", "Ligar / desligar a emulação"),
        ("desligar", "Desligar controles Bluetooth"),
    };

    public static string Rotulo(string acao) =>
        Acoes.FirstOrDefault(a => a.Acao == acao).Rotulo ?? acao;

    private readonly HwndSource _fonte;
    private readonly Dictionary<int, string> _ids = new();
    private int _proximoId = 1;

    /// <summary>Ações cujo registro o Windows recusou (outro programa já usa).</summary>
    public HashSet<string> Falhas { get; } = new();

    public event Action<string>? Disparou;

    public AtalhosGlobais()
    {
        var p = new HwndSourceParameters("DSE.Atalhos")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: janela só de mensagens
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _fonte = new HwndSource(p);
        _fonte.AddHook(Gancho);
    }

    private IntPtr Gancho(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _ids.TryGetValue(wParam.ToInt32(), out var acao))
        {
            handled = true;
            try { Disparou?.Invoke(acao); }
            catch (Exception ex) { DseLog.Write($"[atalhos] erro ao executar {acao}: {DseLog.Fmt(ex)}"); }
        }
        return IntPtr.Zero;
    }

    /// <summary>Troca todos os registros pelos do mapa (vazio = suspende, usado durante a captura).</summary>
    public void Aplicar(IReadOnlyDictionary<string, AtalhoTeclado> mapa)
    {
        foreach (var id in _ids.Keys) UnregisterHotKey(_fonte.Handle, id);
        _ids.Clear();
        Falhas.Clear();
        foreach (var (acao, t) in mapa)
        {
            if (t.Vk == 0) continue;
            int id = _proximoId++;
            if (RegisterHotKey(_fonte.Handle, id, t.Mods | ModNoRepeat, t.Vk))
                _ids[id] = acao;
            else
            {
                Falhas.Add(acao);
                DseLog.Write($"[atalhos] o Windows recusou {Texto(t)} ({acao}): outro programa já usa");
            }
        }
    }

    /// <summary>"Ctrl+Alt+D". Tecla pela posição física (ABNT2: Ctrl+Alt+2 continua "2").</summary>
    public static string Texto(AtalhoTeclado t)
    {
        var sb = new StringBuilder();
        if ((t.Mods & ModCtrl) != 0) sb.Append("Ctrl+");
        if ((t.Mods & ModAlt) != 0) sb.Append("Alt+");
        if ((t.Mods & ModShift) != 0) sb.Append("Shift+");
        if ((t.Mods & ModWin) != 0) sb.Append("Win+");
        sb.Append(NomeTecla(t.Vk));
        return sb.ToString();
    }

    public static string NomeTecla(int vk)
    {
        var k = KeyInterop.KeyFromVirtualKey(vk);
        if (k >= Key.D0 && k <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return "Num " + (k - Key.NumPad0);
        if (k >= Key.A && k <= Key.Z) return k.ToString();
        if (k >= Key.F1 && k <= Key.F24) return k.ToString();
        switch (k)
        {
            case Key.Space: return "Espaço";
            case Key.Enter: return "Enter";
            case Key.Tab: return "Tab";
            case Key.Back: return "Backspace";
            case Key.Delete: return "Del";
            case Key.Insert: return "Ins";
            case Key.Home: return "Home";
            case Key.End: return "End";
            case Key.PageUp: return "PgUp";
            case Key.PageDown: return "PgDn";
            case Key.Left: return "←";
            case Key.Right: return "→";
            case Key.Up: return "↑";
            case Key.Down: return "↓";
            case Key.Pause: return "Pause";
            case Key.Multiply: return "Num *";
            case Key.Add: return "Num +";
            case Key.Subtract: return "Num -";
            case Key.Divide: return "Num /";
            case Key.Decimal: return "Num ,";
        }
        // Teclas de símbolo: o caractere que a tecla escreve no layout atual.
        uint ch = MapVirtualKey((uint)vk, 2) & 0x7FFF;
        if (ch > 32) return char.ToUpperInvariant((char)ch).ToString();
        return k.ToString();
    }

    public void Dispose()
    {
        foreach (var id in _ids.Keys) UnregisterHotKey(_fonte.Handle, id);
        _ids.Clear();
        _fonte.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
