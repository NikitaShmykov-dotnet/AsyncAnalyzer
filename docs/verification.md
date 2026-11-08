# Проверка

Локально использован .NET SDK 10.0.403 из `.sdk` в каталоге проекта.
Команды ниже предполагают, что SDK доступен как `dotnet`.

| Команда из каталога `AsyncGuard` | Результат |
| --- | --- |
| `dotnet restore` | Зависимости восстановлены |
| `dotnet build -c Release --no-restore` | 0 предупреждений, 0 ошибок |
| `dotnet test -c Release --no-build --filter 'Category!=Measurement'` | 22 теста прошли |
| `dotnet format --no-restore --verify-no-changes` | Изменения не требуются |
| `dotnet pack src/AsyncAnalyzer -c Release -o artifacts/feed` | Создан пакет с DLL в `analyzers/dotnet/cs` и README |
| `pwsh -NoProfile -File demo/verify.ps1` | Свежий пакет загружен через `PackageReference`. Вариант Before дал AG001, AG002 и AG003. Вариант Fixed собрался без предупреждений и вывел `Processed: 42` |

Тесты проверяют точные диапазоны и сообщения, подавление через pragma
и `.editorconfig`, сгенерированный и неполный исходник. Для доступных
исправлений тесты применяют действие Roslyn, повторно компилируют документ
и проверяют, что исправленная диагностика больше не появляется. Тест AG003
также сверяет привязку generic-вызова и порядок выражений аргументов.

## Подготовка исправления AG003

```powershell
dotnet test -c Release --no-build --filter 'Category=Measurement' --logger 'console;verbosity=detailed'
```

Один сгенерированный файл с 5 000 простых методов и одним вызовом с AG003.
Перед каждым замером создаётся новая рабочая область, анализ уже выполнен.
Замер включает регистрацию действия и диагностику изменённого документа.
Применение действия, форматирование и интерфейс IDE в замер не входят.
В той же среде, что указана ниже, первый вызов занял 466,6 мс. Пять следующих
заняли 114,6–297,2 мс с медианой 225,1 мс. Порога в тесте нет. Эти числа
относятся к синтетическому файлу.

## Время анализатора

Один прогон компиляции варианта Before, один входной C#-файл и три
срабатывания. Windows 11 x64, Core i7-8750H (6 ядер, 12 потоков),
31,8 ГиБ RAM, SDK 10.0.403. После запуска smoke-проверки в PowerShell:

```powershell
$package = Get-ChildItem artifacts/feed -Filter 'AsyncAnalyzer.*-smoke.*.nupkg' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$version = $package.BaseName.Substring('AsyncAnalyzer.'.Length)
dotnet build demo/Consumer.csproj -c Release -t:Rebuild -p:DemoVariant=Before `
    "-p:AsyncAnalyzerVersion=$version" -p:ReportAnalyzer=true -p:UseSharedCompilation=false -v:d
```

Компилятор сообщил 0,025 с для AsyncAnalyzer и 0,293 с для всех анализаторов.
Замер относится к этому небольшому входу. Замеров на больших решениях нет.

Отдельный потребитель `net9.0` собран с .NET SDK 9.0.312 и пакетом
`AsyncAnalyzer.0.1.0`. Получены AG001, AG002 и AG003. Для проверки исправлений
использован `AdhocWorkspace` на Roslyn 4.8. Visual Studio Community 2022
17.14.37111.16 запустилась, но открыть проект через управление окном
не удалось: координаты окна были недоступны для ввода. Загрузка провайдера в редакторе остаётся непроверенной.
Шаблон CI проверен по локальным командам. Запуска в GitHub Actions не было.

Git-метаданных в рабочей папке нет. Итоговый просмотр выполнен по
содержимому файлов проекта.
