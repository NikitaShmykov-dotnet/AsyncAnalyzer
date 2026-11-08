# Демо

Понадобится SDK из `global.json`. Запускайте команды из каталога `AsyncGuard`.

```sh
dotnet pack src/AsyncAnalyzer -c Release -o artifacts/feed
dotnet build demo/Consumer.csproj -c Release -t:Rebuild -p:DemoVariant=Before
```

Потребитель восстанавливает пакет из `artifacts/feed`. В
[исходном варианте](../demo/Before/Program.cs) сборка успешна с тремя
предупреждениями.

| Диагностика | Место |
| --- | --- |
| AG001 | `pending.Result` |
| AG002 | `RefreshAsync` |
| AG003 | `FormatAsync` |

В IDE с поддержкой Roslyn примените **Await the task** и
**Pass the cancellation token**. В `RefreshAsync` вручную замените `void`
на `Task`. У этого метода в демо нет вызывающего кода. В приложении
проверьте места вызова и добавьте ожидание там, где нужно получить результат
или исключение.

Из CLI тот же результат можно посмотреть в
[готовом варианте](../demo/Fixed/Program.cs).

```sh
dotnet build demo/Consumer.csproj -c Release -t:Rebuild -p:DemoVariant=Fixed
dotnet run --project demo/Consumer.csproj -c Release --no-build -p:DemoVariant=Fixed
```

Сборка без предупреждений. Программа печатает `Processed: 42`.

После изменения анализатора используйте `pwsh -NoProfile -File demo/verify.ps1`.
Скрипт пакует новую версию, поэтому локальный кеш NuGet не подставит старую
сборку с тем же номером.
