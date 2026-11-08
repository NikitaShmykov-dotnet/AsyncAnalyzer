# AsyncAnalyzer

Три предупреждения Roslyn для асинхронного C#. Например, в таком коде токен
есть у вызывающего метода, но операция чтения его не получает.

```csharp
async Task<int> CountAsync(CancellationToken cancellationToken)
{
    var pending = ReadAsync();
    var count = pending.Result;
    return count;
}

Task<int> ReadAsync(CancellationToken token = default)
{
    token.ThrowIfCancellationRequested();
    return Task.FromResult(42);
}
```

AG003 предлагает передать `token: cancellationToken` в `ReadAsync`. AG001
предлагает заменить `pending.Result` на `await pending`. После этих двух
правок вызывающий метод передаёт отмену и не блокирует поток в ожидании задачи.

| Код | Что проверяем | Исправление |
| --- | --- | --- |
| AG001 | `Result`, перегрузки `Wait` и прямая цепочка `GetAwaiter().GetResult()` у `Task` и `Task<T>` внутри async-функции | `await` для `Task<T>.Result` в прямом инициализаторе локальной переменной |
| AG002 | `async void` у методов, локальных функций и анонимных функций | Сигнатуру и места вызова меняет разработчик |
| AG003 | В вызове с однозначной привязкой пропущен один необязательный `CancellationToken`. У ближайшей функции есть ровно один видимый параметр этого типа, передаваемый по значению | Именованный аргумент в конце, если сохраняется выбранный метод |

Все три правила по умолчанию выдают предупреждения. Можно настроить
`dotnet_diagnostic.AG003.severity` в `.editorconfig` или применить
`#pragma warning disable AG003` на нужном участке.

AG001 проверяет ближайшую функцию и ограничен `Task` и `Task<T>`. Вызовы
через `ValueTask`, `ConfigureAwait` и сохранённый в переменную awaiter
остаются за пределами правила. Для AG002 сигнатура `(object, EventArgs)`
или сигнатура с наследником `EventArgs` считается обработчиком независимо
от подписки на событие. AG003 берёт токены только из параметров ближайшей
функции. Захваченные токены внешней функции, поля и локальные переменные
не рассматриваются.

## Сборка

Из каталога `AsyncGuard` с [.NET SDK 10.0.403](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
или более свежим патчем той же ветки SDK.

```sh
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --filter 'Category!=Measurement'
dotnet format --no-restore --verify-no-changes
```

Целевая платформа анализатора — `netstandard2.0`, версия API Roslyn — 4.8.
Команды сборки и демо используют .NET 10. Для работы IDE с исправлениями
нужен хост Roslyn с поддержкой code fixes. Доступность каждого исправления
проверяется отдельно. Если окружающий вызов захватывает исходный текст через `CallerArgumentExpression`, исправление пропускается. Пакетная команда Fix All не предоставляется.

## Демо из локального NuGet

```sh
dotnet pack src/AsyncAnalyzer -c Release -o artifacts/feed
dotnet build demo/Consumer.csproj -c Release -t:Rebuild -p:DemoVariant=Before
dotnet run --project demo/Consumer.csproj -c Release -p:DemoVariant=Fixed
```

В `Before` появляются AG001, AG002 и AG003. Вариант `Fixed` собирается
без этих предупреждений и печатает `Processed: 42`.
Команда `pwsh -NoProfile -File demo/verify.ps1` проверяет оба варианта
с новой версией локального пакета при каждом запуске.

## CI

Шаблон находится в [ci/async-analyzer.yml](ci/async-analyzer.yml). Чтобы включить его
в общем репозитории, скопируйте файл в корневую
`.github/workflows/async-analyzer.yml`. Шаблон рассчитан на проект в каталоге
`AsyncGuard` относительно корня репозитория.

- [Демо и правки](docs/demo.md)
- [Отказы и пограничные случаи](docs/failure-scenarios.md)
- [Границы диагностик](docs/adr/001-diagnostics.md)
- [Безопасность исправлений](docs/adr/002-fixes.md)
- [Совместимость пакета](docs/adr/003-package.md)
- [Проверка](docs/verification.md)
