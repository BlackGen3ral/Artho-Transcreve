# Artho Transcreve

Aplicativo para Windows e extensão para Chrome, Edge e Brave. O texto aparece no app em blocos de cerca de quatro segundos de áudio, mais o tempo de reconhecimento. Você continua ouvindo a aba durante a captura.

[Baixar o app para Windows e a extensão](https://github.com/BlackGen3ral/Artho-Transcreve/raw/refs/heads/main/downloads/Artho-Transcreve-Windows.zip)

## Como usar

1. Extraia tudo do ZIP para uma pasta. Abra **Artho Transcreve.exe** dentro de **app**. O executável é para Windows x64. Se o Windows indicar que faltam componentes, instale os runtimes x64 **.NET Desktop Runtime 10** e **ASP.NET Core Runtime 10**. [Downloads do .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)
2. No app, clique em **Baixar motor**. Esse passo usa internet e baixa cerca de 60 MB para o seu perfil do Windows. Deixe o app aberto.
3. Abra `chrome://extensions`, `edge://extensions` ou `brave://extensions`, conforme seu navegador. Ative **Modo de desenvolvedor**, selecione **Carregar sem compactação** e indique a pasta **extensao** extraída do ZIP. Use um navegador Chromium atualizado.
4. Abra o [TikTok](https://www.tiktok.com/) no navegador e dê play em um vídeo com som. Clique no ícone **Artho Transcreve para TikTok** no menu de extensões.
5. Clique em **Copiar código** no app e cole o código na extensão. Clique em **Iniciar transcrição**. O navegador guarda o código após a primeira conexão bem-sucedida.
6. Na extensão, clique em **Parar transcrição** para concluir os trechos pendentes. Para encerrar de imediato, clique em **Parar captura** no app. Use **Copiar texto** ou **Salvar TXT** para guardar a transcrição.

O idioma padrão é português. Escolha **Detectar idioma**, **Inglês** ou **Espanhol** antes de iniciar se o vídeo estiver em outro idioma. A precisão varia com o som, a música e a fala. O app captura a aba do TikTok selecionada, incluindo novos vídeos do feed enquanto a captura estiver ativa. Capturas simultâneas, o app móvel e o áudio de outros programas ficam fora deste fluxo.

## Atualizações automáticas

Quem instalou a versão **1.0.0** precisa instalar a **1.1.0** uma vez pelo ZIP. Feche o app, extraia por cima da instalação anterior e clique em **Recarregar** na página de extensões do navegador.

A partir da versão **1.1.0**, o app busca novas versões no GitHub ao abrir e a cada 15 minutos. Ele instala a atualização e reinicia quando a captura está parada e a janela está sem transcrição. Se você quiser guardar o texto, use **Salvar TXT** antes de limpar a janela. Também é possível fechar o app e deixar a atualização para a próxima abertura.

Mantenha as pastas **app** e **extensao** juntas e deixe o navegador carregar a extensão desse mesmo local. O app atualiza os arquivos da extensão junto com os seus arquivos. A extensão verifica a versão local ao abrir e a cada minuto com o navegador aberto e se recarrega quando estiver ociosa.

## Processamento de áudio

A extensão entrega apenas trechos de áudio ao app pela porta local `127.0.0.1:48721`, com um código de pareamento. A transcrição usa o whisper.cpp no PC. O motor recebe trechos WAV temporários que o app remove após processar. O texto fica na janela até ser copiado, exportado ou descartado. O primeiro download obtém e verifica o motor e o modelo de fontes fixas.

## Compilar

No PowerShell, com o SDK .NET 10 instalado, rode:

```powershell
dotnet publish .\src\Artho.Transcreve.csproj -c Release -r win-x64 --self-contained false -o .\dist\app
```

Distribua a pasta publicada `app` junto com a pasta `extensao`. O motor e o modelo são instalados pelo próprio app no primeiro uso.

Para publicar uma nova versão, aumente o mesmo número de versão em `src/Artho.Transcreve.csproj` e `extensao/manifest.json`. Execute `pwsh -File ./empacotar.ps1` e publique o ZIP em `downloads/Artho-Transcreve-Windows.zip` e o arquivo `update.json` na raiz de `main`, no mesmo commit que as alterações. O manifesto contém o tamanho e o SHA-256 do ZIP distribuído.

[whisper.cpp](https://github.com/ggml-org/whisper.cpp) · [Modelo Whisper Base quantizado](https://huggingface.co/ggerganov/whisper.cpp/blob/main/ggml-base-q5_1.bin)
