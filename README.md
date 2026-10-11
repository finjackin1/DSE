# DSE

Suporte plug-and-play a controles PlayStation no Windows.

O DSE faz seu **DualShock 4** ou **DualSense** funcionar em qualquer jogo de PC,
emulando um controle **Xbox 360** ou um **DualShock 4** virtual. Conecta por USB
ou Bluetooth, reconhece o controle sozinho e não precisa de configuração.

---

## O que ele faz

- **Emulação por perfil**: Xbox 360 (compatível com praticamente todo jogo de PC)
  ou DualShock 4 (mantém giroscópio, touchpad e lightbar nos jogos que suportam).
- **Troca de perfil na hora**, sem fechar o jogo.
- **Vibração dos jogos** repassada ao controle, nos dois perfis, por USB ou
  Bluetooth.
- **Controle da lightbar**: cor fixa por perfil ou repasse da cor que o jogo pedir.
- **Nível de bateria** na tela, com aviso quando está acabando.
- **Desliga o controle** por atalho, de verdade — sem precisar do console.
- **Desativa a emulação por controle**, devolvendo o aparelho ao Windows quando
  você quiser usá-lo nativamente (em jogos da Steam com suporte a DualSense, por
  exemplo).
- **Desliga sozinho** o controle esquecido ligado depois de 10 minutos parado.
- **Desliga o Bluetooth automaticamente** quando você pluga o cabo USB no mesmo
  controle, evitando conexão duplicada.
- **Lembra cada controle**: o perfil e o modo da luz voltam como você deixou,
  mesmo depois de reiniciar o PC.
- **Atalhos do teclado** (opcionais) para mostrar a janela, trocar o perfil,
  ligar/desligar a emulação e desligar os controles, mesmo com o DSE escondido.
- **Se atualiza sozinho**: avisa quando sai versão nova e instala com um clique.

## Atalhos no controle

| Atalho | O que faz |
|---|---|
| `Share` + `Options` | Alterna entre os perfis Xbox 360 e DualShock 4 |
| `PS` segurado por 1s | Desliga o controle (só por Bluetooth) |
| `Options` + `PS` por 1s | Liga/desliga a emulação daquele controle |
| Clique no touchpad + `PS` por 1s | Alterna o LED entre a cor do perfil e a cor que o jogo pedir |

Os atalhos do `PS` continuam funcionando mesmo com a emulação desativada,
enquanto o DSE estiver aberto — é assim que dá pra reativar a emulação sem tocar
no teclado.

O controle avisa pelo tato qual atalho pegou: ativar a emulação dá um tremor
grave subindo, desativar dá o mesmo tremor descendo, e a troca do modo do LED dá
dois toques secos. **Solte os botões assim que sentir a vibração** — segurar o
`PS` por cerca de 10 segundos desliga o controle, e isso é do próprio aparelho,
não do DSE.

## Na janela

- À esquerda, o **perfil** (Xbox 360, DualShock 4 ou desativar a emulação) e a
  **luz** (cor do perfil ou cor do jogo) do controle escolhido. Teclas `1`, `2`
  e `0` também trocam o perfil.
- À direita, os **controles conectados**, com bateria e botão de desligar
  (Bluetooth), e a imagem do controle: clique no **corpo** para ligar/desligar a
  emulação e no **touchpad** para alternar a luz.
- Sem controle conectado, a janela mostra como parear e abre as configurações de
  Bluetooth do Windows num clique.
- O botão **?** abre a lista completa de atalhos do controle; o botão de
  **opções** abre: iniciar com o Windows (e iniciar mais rápido), iniciar
  minimizado, desligar o Bluetooth ao plugar o cabo, procurar atualização e os
  atalhos do teclado.
- O **X** esconde o DSE na bandeja; segurar o X por 1 segundo fecha de vez.
- Na bandeja, um clique abre a janela e outro clique fecha; o duplo clique só
  abre. O **botão direito** mostra os controles conectados com a bateria
  e dá acesso às opções, à atualização e ao fechar.

---

## Instalação

1. Baixe o DSE portable na aba [Releases](https://github.com/finjackin1/DSE/releases).
2. Extraia em qualquer pasta e rode o `DSE.App.exe`.
3. Na primeira execução, o assistente instala os drivers que faltarem.

Não há instalador: a pasta é autocontida e pode ficar onde você quiser.

### Compilando do código-fonte

O portable publicado nas Releases sai deste mesmo código. Para gerar o seu:

```
git clone https://github.com/finjackin1/DSE.git
cd DSE
powershell -ExecutionPolicy Bypass -File ferramentas\empacotar.ps1
```

O resultado fica em `dist\DSE\DSE.App.exe` (e o zip em `dist\`). Para apenas
rodar em desenvolvimento: `dotnet run --project src\DSE.App`.

Precisa do [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
(o SDK, não só o Runtime). Os pacotes NuGet são restaurados automaticamente.
A solução também abre direto no Visual Studio 2022 pelo `DSE.sln`.

### Requisitos

| Item | Observação |
|---|---|
| Windows 10/11 (64 bits) | — |
| [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) | Se faltar, o Windows avisa com o link ao abrir o programa |
| [ViGEmBus](https://github.com/nefarius/ViGEmBus) | Cria os controles virtuais — o assistente do DSE instala |
| [HidHide](https://github.com/nefarius/HidHide) | Esconde o controle físico pra o jogo não ver dois — o assistente instala |

Depois de instalar os drivers pela primeira vez, **reinicie o computador**. O
Windows precisa disso para carregá-los, e sem o reinício a criação do controle
virtual pode falhar.

---

## Limitações conhecidas

- **Segundo dedo do touchpad**: usando um DualSense no perfil DualShock 4, apenas
  um ponto de toque é repassado ao controle virtual. Os dois controles rastreiam
  dois dedos, mas o caminho de decodificação do DualSense encaminha só o
  primeiro. Com um DualShock 4 físico os dois passam normalmente.
- **Desligar o controle** só funciona por Bluetooth. No cabo não existe desligar:
  o USB alimenta o aparelho.
- **Luzes brancas do DualSense**: ao desativar a emulação, o controle volta a ser
  visível para o sistema e a Steam acende o indicador de jogador. É o mesmo
  comportamento de conectar o controle sem o DSE.
- A emulação é de **Xbox 360 ou DualShock 4** — não de DualSense. Para jogos com
  suporte nativo a DualSense, desative a emulação daquele controle e use-o direto.

---

## Atualização

O DSE consulta as releases deste repositório no prazo escolhido nas opções
(todo dia, toda semana — o padrão —, todo mês ou nunca), e também quando você
clica em **Procurar agora**. Se houver versão nova, aparece um aviso na bandeja e
uma seta verde na barra de título; ele não interrompe o jogo com perguntas.

Ao clicar no aviso, o DSE pergunta se pode atualizar:

- **Sim**: baixa o zip da release, confere o tamanho e o SHA-256 publicados pelo
  GitHub, pede permissão de administrador, fecha, troca os arquivos e abre de
  novo na versão nova. Perfis, luzes, atalhos e opções são mantidos.
- **Não**: aquela versão deixa de ser avisada (dá pra instalar depois pelo
  **Procurar agora**).

Essa consulta e o download são os **únicos** acessos à internet do programa; ele
não envia nada sobre você ou sobre o seu uso, e falha em silêncio se não houver
conexão. Quem preferir pode escolher **nunca** e atualizar à mão pela aba
Releases.

## Relatando um problema

O DSE tem um log de diagnóstico **desligado por padrão**. Para ativá-lo, crie um
arquivo (ou pasta) vazio chamado `log` na mesma pasta do `DSE.App.exe`, reproduza
o problema e anexe o `dse.log` que aparecer ali. O log recomeça a cada execução e
não é gravado enquanto o marcador não existir.

Ele registra conexões, trocas de perfil e falhas — nada sobre o que você joga ou
digita.

---

## Como foi feito

O DSE foi desenvolvido com apoio de inteligência artificial: o código foi escrito
em conversa com o **Claude**, da Anthropic (no Claude Code). As decisões de projeto, o desenho da
interface, os testes em hardware real (DualShock 4 e DualSense, por USB e
Bluetooth) e o rumo de cada funcionalidade são de [finjackin](https://github.com/finjackin1).

---

## Licença

Copyright © 2026 finjackin.

O DSE é software livre, distribuído sob a **GNU General Public License v3.0** —
os termos completos estão em [LICENSE](LICENSE). Você pode usar, estudar,
modificar e redistribuir o programa; se distribuir uma versão modificada, ela
precisa vir também sob a GPLv3, com o código-fonte disponível.

As bibliotecas e drivers de terceiros usados pelo DSE, com suas respectivas
licenças, estão listados em [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
