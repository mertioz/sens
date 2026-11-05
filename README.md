# Sens

Sens est un traducteur pour Windows x64. Il utilise les versions de Windows prises en charge par [.NET 10](https://learn.microsoft.com/fr-fr/dotnet/core/install/windows#supported-versions).

## Installer

Ouvre le dernier résultat réussi dans [Actions → Windows](https://github.com/mertioz/sens/actions/workflows/windows.yml) et télécharge une archive dans **Artifacts**.

- **Sens-Windows-x64-autonome** contient le runtime .NET. Choisis cette édition si tu installes Sens sur un autre PC.
- **Sens-Windows-x64-legere** utilise le [runtime Windows Desktop .NET 10 x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) déjà installé sur le PC.

Décompresse l’archive, puis lance cette commande dans PowerShell depuis son dossier.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-Sens.ps1
```

Tu peux aussi lancer directement `dist\Sens\Sens.exe` pour essayer Sens. L’installation ajoute les raccourcis et le démarrage automatique.

## Utiliser

Sens affiche un petit bouton **Traduire** à côté du menu de clic droit quand une application Windows expose le texte sélectionné. La traduction s’ouvre près de la sélection. Écris une réponse sous la traduction et appuie sur **Entrée** : Sens traduit ta réponse dans la langue du texte d’origine. Tu peux ensuite la copier dans la conversation. Si un mot est trop ambigu pour identifier sa langue, indique-la dans le panneau.

Sens se lance en arrière-plan à l’ouverture de la session Windows. **Ctrl + Alt + T** ou l’icône près de l’horloge ouvre la saisie manuelle. Dans la roue dentée, la **Consigne de style de l’IA** règle le ton des traductions et des réponses, par exemple « Ton chaleureux, phrases courtes, tutoiement ». Laisse le champ vide pour garder le style d’origine. La croix masque la fenêtre et **Alt + F4** sur le panneau arrête Sens.

- **Codex** réutilise la session ChatGPT de Codex CLI et son quota. Sens cherche le CLI dans le PATH, dans les installations Windows et npm, ainsi que dans l’application Codex du Microsoft Store si son binaire est accessible. Au besoin, installe le [CLI officiel pour Windows](https://learn.chatgpt.com/docs/codex/cli), puis lance `codex login` sur ce PC. Pour un emplacement personnalisé, utilise **Réglages → Codex → Choisir codex.exe**. Les modèles affichés dépendent de la session connectée.
- **OpenAI** et **DeepSeek** utilisent leurs [clés OpenAI](https://platform.openai.com/api-keys) ou [clés DeepSeek](https://platform.deepseek.com/api_keys). La [facturation de l’API OpenAI est distincte de ChatGPT](https://help.openai.com/en/articles/9039756-managing-billing-for-chatgpt-and-the-api-platform).
- **Claude** utilise une [clé API Anthropic](https://platform.claude.com/settings/keys) créée dans la console Claude. Sélectionne **Claude** dans les réglages et colle la clé. Tu peux aussi définir `ANTHROPIC_API_KEY` dans ton environnement.

Certains champs et certaines applications n’exposent pas leur sélection à Windows. Dans ce cas, copie le texte et colle-le dans Sens. Les champs identifiés comme mots de passe sont ignorés. Le texte est envoyé au fournisseur uniquement lors d’une demande de traduction. Sens ne conserve pas d’historique local et enregistre les clés API dans le Gestionnaire d’identifiants Windows.

## Compiler et installer

Le projet utilise WPF et .NET 10. La publication produit un exécutable Windows x64 dans `dist/Sens` qui utilise le runtime Windows Desktop .NET 10 installé sur le PC.

```powershell
dotnet build src/Sens -c Release
dotnet publish src/Sens -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o dist/Sens
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Install-Sens.ps1
```

Le script installe l’application et ses raccourcis pour l’utilisateur courant, sans droits administrateur. Pour la désinstaller, ferme-la, puis retire le dossier `%LOCALAPPDATA%\Programs\Sens` et les raccourcis du Bureau, du menu Démarrer et du dossier de démarrage (`shell:startup`). Les préférences sont dans `%LOCALAPPDATA%\Sens`.

## Vérifier

```powershell
dotnet run --project tests/ApiSmoke -c Release
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File tools/smoke-ui.ps1
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File tools/smoke-ui.ps1 -Edge
```

Le test API simule les réponses OpenAI, DeepSeek et Claude. L’option `-- --codex` fait deux requêtes réelles avec la session Codex connectée. Les tests d’interface demandent un bureau interactif et un profil où Sens est fermé et n’a pas encore été configuré. Le workflow Windows teste une installation Codex sans compte, puis démarre les deux éditions de Sens sur un runner Windows indépendant.
