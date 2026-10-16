using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Line = System.Windows.Shapes.Line;
using Microsoft.Win32;
using ModbusScanner.Models;

namespace ModbusScanner;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<RegisterRow> _rows = new();
    private ModbusTcpSimulator? _simulator;

    private async void ToggleSimulator_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_simulator is not null)
            {
                await _simulator.DisposeAsync();
                _simulator = null;
                // Closing the test server invalidates an existing connection to its loopback port.
                if (IsTcp && (HostBox.Text.Trim() is "127.0.0.1" or "localhost") && PortBox.Text.Trim() == "1502")
                {
                    Disconnect();
                    SetStatusText("Нет подтверждённой связи");
                    StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                }
                SimulatorButton.Content = UiText("Запустить TCP симулятор");
                Log("Тестовый TCP симулятор остановлен.");
                return;
            }
            var simulator = new ModbusTcpSimulator(1502);
            simulator.Start();
            _simulator = simulator;
            SimulatorButton.Content = UiText("Остановить TCP симулятор");
            Log("Тестовый Modbus TCP сервер: 127.0.0.1:1502. Для подключения выберите TCP, Host 127.0.0.1, Port 1502.");
        }
        catch (Exception ex) { Log("Симулятор TCP: " + ex.Message); }
    }

    private readonly object _serialGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _tcpGate = new(1, 1);
    private SerialPort? _serial;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transactionId;
    private string _transportContext="";
    private CancellationTokenSource? _scanCts;
    private bool _connected;
    private bool IsTcp => ProtocolBox.SelectedIndex == 1;
    private int ValueMode => ValueTypeBox.SelectedIndex;

    public MainWindow()
    {
        InitializeComponent();
        RegistersGrid.ItemsSource = _rows;
        RefreshComPorts();
        RefreshLanAdapters();
        RestoreLocalSettings();
        InitializeLanguage();
        ProtocolBox_SelectionChanged(this, null!);
        Log("Modbus Scanner запущен. Запись по умолчанию требует подтверждения.");
    }


    private readonly Dictionary<DependencyObject, string> _originalLabels = new();
    private readonly Dictionary<DataGridColumn, string> _originalColumnHeaders = new();
    private static readonly Dictionary<string, string> EnglishLabels = new(StringComparer.Ordinal)
    {
        ["Быстрый старт: для безопасной проверки без оборудования запустите TCP-симулятор, выберите TCP, Host 127.0.0.1, порт 1502, Unit ID 1, FC03, адрес 0 и количество 10. Нажмите «Подключить / проверить», затем «Читать диапазон». Запись в реальное оборудование не требуется."]="Quick start: to test safely without hardware, start the TCP simulator, select TCP, host 127.0.0.1, port 1502, Unit ID 1, FC03, address 0 and quantity 10. Select Connect / test, then Read range. No writing to real equipment is required.",
        ["Безопасность: перед записью остановите мониторинг, поиск регистров или IP-сканирование. Программа блокирует запись во время активного поиска или мониторинга. После записи проверьте значение повторным чтением; при несовпадении проверьте адрес, функцию, тип данных и документацию устройства."]="Safety: stop monitoring, register discovery or IP scanning before writing. The app blocks writes during active scanning or monitoring. After writing, verify by reading back; if values differ, check the address, function, data type and device manual.",
        ["Если устройство не отвечает: проверьте IP/COM-порт, скорость, Unit ID и адрес регистра. Сообщение о слишком коротком ответе означает неполный кадр; повторите проверку связи, осмотрите кабель и настройки шлюза. При сбое обмена программа разрывает соединение, чтобы не использовать повреждённый поток данных."]="If a device does not respond, check its IP/COM port, baud rate, Unit ID and register address. An incomplete response means a truncated frame: test connectivity again, check cabling and gateway settings. After a transport error the app disconnects to avoid reusing a damaged data stream.",
        ["Симулятор: при остановке встроенного сервера подключение к 127.0.0.1:1502 сбрасывается, а статус связи становится неподтверждённым. После повторного запуска симулятора нажмите «Подключить / проверить», чтобы заново проверить соединение."]="Simulator: stopping the built-in server disconnects 127.0.0.1:1502 and marks the connection as unverified. After restarting the simulator, select Connect / test to verify the connection again.",
        ["Разрешить запись"]="Enable writing",
        ["Продолжить остановленный поиск (в текущем сеансе)"]="Resume interrupted scan (this session)",
        ["К записи FC16"]="FC16 write",
        ["Типизированное значение"]="Typed value",
        ["Интерпретация / примечание"]="Interpretation / notes",
        ["Ед."]="Unit",
        ["Множитель"]="Scale",
        ["Смещение"]="Offset",
        ["Пересчёт"]="Scaled value",
        ["Качество"]="Quality",
        ["Протокол"]="Protocol",
        ["Скорость"]="Baud rate",
        ["Стартовый адрес"]="Start address",
        ["Количество"]="Quantity",
        ["Интерпретация"]="Value type",
        ["Период, мс"]="Interval, ms",
        ["Выбранный адрес"]="Selected address",
        ["Новое значение"]="New value",
        ["Формат"]="Format",
        ["Бит"]="Bit",
        ["32-бит"]="32-bit",
        ["Поиск Modbus TCP-устройств в локальной сети"]="Find Modbus TCP devices on the local network",
        ["IP от"]="IP from",
        ["IP до"]="IP to",
        ["Порты через запятую"]="Comma-separated ports",
        ["Тайм-аут, мс"]="Timeout, ms",
        ["Адреса через запятую (до 4, текущая FC)"]="Comma-separated addresses (up to 4, current FC)",
        ["До 100 000 точек; график последних 1000. Значение = Raw × множитель + смещение."]="Up to 100,000 samples; chart displays the last 1,000. Value = Raw × scale + offset.",
        ["Скорости через запятую"]="Comma-separated baud rates",
        ["Чётность"]="Parity",
        ["ID от / до"]="ID from / to",
        ["Выберите результат после остановки поиска, чтобы применить параметры. Результаты поиска появляются в журнале. Для скорости используйте ограниченный диапазон Slave ID и отметьте только реальные скорости, поддерживаемые преобразователем и контроллером."]="Select a result after stopping the scan to apply its settings. Results appear in the log. Use a narrow Slave ID range and only supported baud rates.",
        ["Поиск устройств локальной сети"]="Local network device discovery",
        ["Проверка доступности по TCP и обратного DNS. Тип устройства определяется предположительно по открытым портам, а не гарантированно. Используйте только в своей сети или с разрешения администратора."]="Checks TCP reachability and reverse DNS. Device type is only estimated from open ports. Scan only networks you own or are authorized to test.",
        ["От"]="From",
        ["До"]="To",
        ["Тайм-аут (мс)"]="Timeout (ms)",
        ["Проверяемые порты: 80, 443, 22, 23, 502, 8080, 554, 3389, 445, 9100. Отсутствие ответа не означает, что IP свободен."]="Probed ports: 80, 443, 22, 23, 502, 8080, 554, 3389, 445, 9100. No response does not mean an IP is unused.",
        ["Выберите найденный адрес, чтобы перенести его в настройки Modbus TCP (порт 502)."]="Select a discovered address to use it for Modbus TCP (port 502).",
        ["Пунктирные линии показывают только принадлежность к результатам поиска, НЕ реальные кабельные соединения. Для достоверной топологии потребуются LLDP/SNMP."]="Dotted lines represent logical grouping, NOT actual cable connections. Reliable physical topology requires LLDP/SNMP.",
        ["Разработчик / Контакт: skynet909084@gmail.com"]="Developer / Contact: skynet909084@gmail.com",
        ["Регистры"]="Registers", ["Поиск TCP"]="TCP discovery", ["Снимки / изменения"]="Snapshots / changes",
        ["График / история"]="Chart / history", ["Автопоиск"]="Auto discovery", ["IP-сканер"]="IP scanner",
        ["Список устройств"]="Devices", ["Диагностика сети"]="Network diagnostics", ["Карта сети"]="Network map",
        ["Справка / О программе"]="Help / About", ["Обновить COM"]="Refresh COM",
        ["Сохранить профиль"]="Save profile", ["Загрузить профиль"]="Load profile",
        ["Подключить / проверить"]="Connect / test", ["Читать диапазон"]="Read range",
        ["Автопоиск RTU"]="RTU auto discovery", ["Сканировать регистры"]="Scan registers",
        ["Остановить"]="Stop", ["Экспорт CSV"]="Export CSV", ["▶ Мониторинг"]="▶ Monitor",
        ["■ Стоп"]="■ Stop", ["Бит = 1"]="Bit = 1", ["Бит = 0"]="Bit = 0",
        ["Запустить TCP симулятор"]="Start TCP simulator", ["Остановить TCP симулятор"]="Stop TCP simulator", ["Экспорт карты JSON"]="Export JSON map",
        ["Импорт карты JSON"]="Import JSON map", ["Записать 32-бит (FC16)"]="Write 32-bit (FC16)",
        ["Записать регистр (FC06)"]="Write register (FC06)",
        ["Записать несколько (FC16)"]="Write multiple (FC16)",
        ["Записать Coil (FC05)"]="Write coil (FC05)",
        ["Повторно прочитать выбранный"]="Read selected again", ["Найти узлы"]="Find hosts",
        ["Остановить поиск"]="Stop discovery", ["Сделать снимок"]="Take snapshot",
        ["Сохранить снимок"]="Save snapshot", ["Загрузить и сравнить"]="Load and compare",
        ["История CSV"]="History CSV", ["Очистить историю"]="Clear history",
        ["Обновить адаптеры"]="Refresh adapters", ["Определить мою подсеть"]="Detect my subnet",
        ["Найти устройства"]="Find devices", ["Очистить результаты"]="Clear results",
        ["Ping × 4"]="Ping × 4", ["Мониторинг × 20"]="Monitor × 20",
        ["Трассировка"]="Traceroute", ["TCP-порт"]="TCP port", ["Проверить DNS"]="Check DNS",
        ["Ping шлюза"]="Ping gateway", ["Сравнить шлюз и узел"]="Compare gateway / host",
        ["ARP-таблица"]="ARP table", ["Экспорт отчёта"]="Export report",
        ["Обновить карту по результатам поиска"]="Refresh map from scan",
        ["Сохранить журнал"]="Save log", ["Адрес"]="Address", ["Порт"]="Port",
        ["Ping и трассировка IPv4"]="IPv4 ping and traceroute",
        ["Диагностика сети — пошаговая проверка"]="Network diagnostics — step by step",
        ["Modbus Scanner — руководство"]="Modbus Scanner — User guide",
        ["Язык / Language:"]="Language / Язык:",
        ["Шаг 1. Выберите сетевой адаптер в IP-сканере. Шаг 2. Введите IP устройства или DNS-имя для проверки DNS. Шаг 3. Нажмите нужную проверку; результат появится ниже."]=
            "Step 1: Select the network adapter in IP scanner. Step 2: Enter a device IP, or a host name for DNS. Step 3: Run a test and read the results below.",
        ["Подсказка: Ping — доступность и задержка; Мониторинг — нестабильность за 20 запросов; TCP-порт — принимает ли служба соединения; Сравнить — шлюз и устройство; ARP — изменения MAC между проверками; Экспорт — текстовый отчёт."]=
            "Tip: Ping tests response time; Monitor samples 20 pings; TCP checks whether a service accepts connections; Compare checks gateway and host; ARP detects MAC changes; Export saves a report.",
        ["Потери и задержки показывают симптомы, но не доказывают неисправность кабеля. Промежуточные маршрутизаторы могут не отвечать на ICMP."]=
            "Loss and latency are symptoms, not proof of a bad cable. Intermediate routers may ignore ICMP.",
        ["Диагностика сети — понятная последовательность:"]="Network diagnostics — step-by-step guide:",
        ["1. Подключение: выберите Modbus RTU (COM, скорость, чётность, биты) либо TCP (IP и порт), задайте Unit ID. Нажмите «Подключить / проверить»."]="1. Connection: select Modbus RTU (COM port, baud rate, parity and data bits) or TCP (IP and port). Set Unit ID, then select Connect / test.",
        ["2. Регистры: выберите FC01/02 для битов, FC03/04 для 16-битных регистров, адрес и количество. Читайте диапазон, ищите доступные адреса, используйте интерпретацию чисел и порядок байтов."]="2. Registers: use FC01/02 for coils and discrete inputs, FC03/04 for 16-bit registers. Specify start address and count, then read or scan. Check value types and byte order.",
        ["Адресация Modbus: в программе используются адреса с нуля. Обозначение 40001 в документации обычно означает FC03, адрес 0; 40002 — адрес 1. Некоторые производители используют адреса с единицы, поэтому обязательно сверяйтесь с руководством устройства. Не вводите 40001 вместо 0 без проверки."]="Modbus addressing: this application uses zero-based addresses. Documentation label 40001 typically means FC03 address 0, and 40002 means address 1. Some vendors use one-based addressing: check the device manual before entering an address.",
        ["3. Запись: доступна только при явном разрешении и подтверждении. FC05 — катушка, FC06 — один holding-регистр, FC16 — несколько регистров или 32-битное значение. Сверяйте повторное чтение."]="3. Writing: requires explicit permission and confirmation. FC05 writes a coil, FC06 one holding register, and FC16 multiple registers or a 32-bit value. Verify by reading back.",
        ["4. Карты регистров: импортируйте/экспортируйте JSON. Тип и признак Writable — справочные метаданные; они сами по себе не разрешают запись. Сохраняйте профили подключения."]="4. Register maps: import/export JSON. Type and Writable are descriptive metadata and do not authorize writes. Save connection profiles.",
        ["5. Поиск TCP проверяет Modbus на выбранных портах. IP-сканер ищет TCP-службы в ограниченном диапазоне IPv4 и показывает предполагаемый тип устройства. Для определения точной модели проверьте интерфейс устройства или документацию."]="5. TCP discovery checks Modbus on selected ports. IP scanner searches a bounded IPv4 range for TCP services and suggests device types. Confirm the actual device model using its interface or manual.",
        ["Карта сети: вкладка показывает обнаруженные TCP-узлы, а линии пока обозначают логическую группировку. Реальные соединения между портами коммутаторов не определяются без дополнительных данных (например, LLDP/SNMP). Нажмите на узел карты для переноса IP в Modbus TCP."]="Network map: displays discovered TCP hosts. Lines show logical grouping only, not physical switch links. Real topology requires LLDP/SNMP or other evidence. Click a node to copy its IP into Modbus TCP.",
        ["Шаг 1. Во вкладке «IP-сканер» выберите нужный сетевой адаптер (Wi-Fi или Ethernet). Если устройств несколько, проверьте, что выбран адаптер нужной сети. Нажмите обновление адаптеров при необходимости."]="Step 1: In IP scanner, choose the correct Wi-Fi or Ethernet adapter. Refresh the adapter list if necessary.",
        ["Шаг 2. Откройте «Диагностика сети». В поле «Адрес» введите IPv4 проверяемого устройства. Кнопка «Ping шлюза» сама подставит адрес шлюза выбранного адаптера. «Сравнить шлюз и узел» проверяет оба адреса последовательно."]="Step 2: Open Network diagnostics and enter the device IPv4 address. Ping gateway fills in the selected adapter gateway. Compare gateway / host tests both in sequence.",
        ["Ping × 4: четыре ICMP-запроса с тайм-аутом 1 с. Получено 4/4 — устройство отвечает; потери 25% — один запрос не получил ответа. Устройство может блокировать ICMP и при этом работать по TCP."]="Ping × 4: four ICMP probes with 1-second timeout. 4/4 replies means the host responded; 25% loss means one unanswered probe. A host can block ICMP while TCP still works.",
        ["Мониторинг × 20: двадцать запросов с паузами 1 с, статистика потерь и график. Зелёный — ответ до 100 мс, оранжевый — свыше 100 мс, красный — нет ответа. Высота столбца ограничена 500 мс. Это ориентиры, а не универсальные пороги аварии."]="Monitor × 20: twenty probes with 1-second pauses, packet-loss statistics and a chart. Green: up to 100 ms; orange: above 100 ms; red: no reply. Bars are capped at 500 ms. These are guides, not universal fault thresholds.",
        ["TCP-порт: введите IPv4 и порт (обычно 502 для Modbus TCP), затем нажмите кнопку. Четыре подключения покажут доступность службы и время установки TCP-соединения. Это не проверка корректности регистров Modbus."]="TCP port: enter IPv4 and port (usually 502 for Modbus TCP). Four connection attempts show service reachability and TCP handshake time, not Modbus register correctness.",
        ["Проверить DNS: в поле «Адрес» можно ввести имя сайта или устройства. Программа покажет найденные адреса и время поиска. Если введён числовой IP, полноценная проверка разрешения имени не выполняется."]="Check DNS: enter a host name in Address. The app shows resolved addresses and lookup time. Entering a numeric IP does not test forward DNS resolution.",
        ["Трассировка: показывает до 20 промежуточных маршрутизаторов до адреса. Пропуск ответа или звёздочка не обязательно означает неисправность: ICMP может фильтроваться или ограничиваться."]="Traceroute: shows up to 20 hops. A missing reply does not necessarily mean failure; routers may filter or rate-limit ICMP.",
        ["Сравнить шлюз и узел: по пять Ping к шлюзу и конечному устройству. Если шлюз отвечает быстро, а устройство медленно, проверьте маршрут, нагрузку и само устройство. Измерения последовательные; по ним нельзя определить неисправный порт коммутатора."]="Compare gateway / host: five pings to each. If the gateway is fast and the target slow, inspect routing, load and the host. Tests run sequentially and cannot identify a faulty switch port.",
        ["ARP-таблица: нажмите дважды с интервалом времени. Если для одного IP изменился MAC, появится предупреждение. Это лишь повод проверить DHCP, резервирование адресов и журналы роутера; ARP-кэш может быть неполным или устаревшим."]="ARP table: run twice at different times. A changed MAC for the same IP triggers a warning, not proof of an IP conflict. Check DHCP reservations and router logs; ARP cache may be stale or incomplete.",
        ["Остановить: отменяет текущую диагностику. Экспорт отчёта: сохраняет видимый текст результатов в TXT. Новая проверка заменяет прежний текст, поэтому нужный отчёт сохраните заранее."]="Stop cancels the current test. Export report saves visible results as TXT. A new test replaces the previous output, so export first if you need to keep it.",
        ["Быстрый поиск причины: (1) Ping шлюза; (2) Ping устройства; (3) TCP-порт 502; (4) сравнение задержек; (5) при периодических сбоях — мониторинг × 20 и ARP; (6) сохраните отчёт. Если Ping не проходит, но TCP работает, это может быть фильтрация ICMP, а не обрыв связи."]="Quick troubleshooting: (1) Ping gateway; (2) Ping host; (3) test TCP port 502; (4) compare latency; (5) for intermittent issues use Monitor × 20 and ARP; (6) export a report. If TCP works but Ping fails, ICMP filtering may be the cause.",
        ["6. Симулятор: запустите локальный тестовый сервер, выберите TCP, адрес 127.0.0.1, порт 1502. Используйте его для безопасного тестирования команд без оборудования."]="6. Simulator: start the local test server, choose TCP, host 127.0.0.1 and port 1502. Test commands without physical equipment.",
        ["7. Снимки, графики, журнал и автопоиск: фиксируйте значения, отслеживайте изменения, экспортируйте журнал и останавливайте длительное сканирование кнопкой остановки."]="7. Snapshots, charts, logs and auto discovery: capture values, track changes, export logs and stop long scans when needed.",
        ["Проект: Modbus Scanner. Автор проекта: Blood Sky / skynet909084-boop. Платформа: Windows, .NET 8 WPF."]="Project: Modbus Scanner. Author: Blood Sky / skynet909084-boop. Platform: Windows, .NET 8 WPF.",
        ["Важно: не сканируйте чужие сети и не записывайте значения на работающем оборудовании без понимания последствий."]=
            "Important: Scan only networks you are authorized to test. Do not write values to production devices without understanding the consequences."
    };
    private static string LanguagePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BloodSky", "ModbusScanner", "language.txt");

    private void CaptureLabels(DependencyObject root)
    {
        if (root is DataGrid grid)
            foreach (var column in grid.Columns)
                if (column.Header is string columnTitle) _originalColumnHeaders[column] = columnTitle;
        if (root is Button b && b.Content is string buttonText) _originalLabels[b] = buttonText;
        if (root is TabItem tab && tab.Header is string header) _originalLabels[tab] = header;
        if (root is TextBlock label && !string.IsNullOrWhiteSpace(label.Text) &&
            System.Windows.Data.BindingOperations.GetBindingBase(label, TextBlock.TextProperty) is null &&
            label != StatusText)
            _originalLabels[label] = label.Text;
        if (root is CheckBox check && check.Content is string checkText) _originalLabels[check] = checkText;
        if (root is System.Windows.Controls.HeaderedContentControl headered && headered.Header is string title && root is not TabItem) _originalLabels[headered] = title;
        if (root is System.Windows.Controls.HeaderedItemsControl headeredItems && headeredItems.Header is string itemsTitle) _originalLabels[headeredItems] = itemsTitle;
        // Logical traversal also includes content of tabs that have never been opened.
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject element) CaptureLabels(element);
    }

    private string UiText(string russian, string english) => LanguageBox?.SelectedItem is ComboBoxItem { Tag: "en" } ? english : russian;

    private string UiText(string russian) => LanguageBox?.SelectedItem is ComboBoxItem { Tag: "en" } && EnglishLabels.TryGetValue(russian, out var english) ? english : russian;

    private void ApplyLanguage(string language)
    {
        foreach (var column in _originalColumnHeaders)
            column.Key.Header = language == "en" && EnglishLabels.TryGetValue(column.Value, out var translatedHeader)
                ? translatedHeader : column.Value;
        foreach (var item in _originalLabels)
        {
            string value = language == "en" && EnglishLabels.TryGetValue(item.Value, out var translated)
                ? translated : item.Value;
            switch (item.Key)
            {
                case Button button: button.Content = value; break;
                case TabItem tab: tab.Header = value; break;
                case TextBlock label: label.Text = value; break;
                case CheckBox check: check.Content = value; break;
                case System.Windows.Controls.HeaderedContentControl headered: headered.Header = value; break;
                case System.Windows.Controls.HeaderedItemsControl headeredItems: headeredItems.Header = value; break;
            }
        }
        SimulatorButton.Content = _simulator is not null
            ? UiText("Остановить TCP симулятор")
            : UiText("Запустить TCP симулятор");
        string currentStatus = StatusText.Text;
        string originalStatus = EnglishStatusText.FirstOrDefault(pair => pair.Value == currentStatus).Key ?? currentStatus;
        if (EnglishStatusText.ContainsKey(originalStatus))
            SetStatusText(originalStatus);
        else
        {
            // Preserve live operation details when switching between Russian and English.
            foreach (var pair in LiveStatusPrefixes)
            {
                if (currentStatus.StartsWith(pair.Key, StringComparison.Ordinal))
                {
                    StatusText.Text = language == "en" ? pair.Value + currentStatus[pair.Key.Length..] : currentStatus;
                    break;
                }
                if (currentStatus.StartsWith(pair.Value, StringComparison.Ordinal))
                {
                    StatusText.Text = language == "ru" ? pair.Key + currentStatus[pair.Value.Length..] : currentStatus;
                    break;
                }
            }
        }
    }

    private static readonly Dictionary<string, string> LiveStatusPrefixes = new(StringComparer.Ordinal)
    {
        ["Связь подтверждена · Unit "]="Connection verified · Unit ",
        ["Мониторинг · OK · "]="Monitoring · OK · ",
        ["Прочитано: "]="Read: ",
        ["IP-поиск "]="IP scan ",
        ["RTU "]="RTU ",
    };

    private static readonly Dictionary<string, string> EnglishStatusText = new(StringComparer.Ordinal)
    {
        ["Профиль загружен · подключение не проверено"]="Profile loaded · connection not tested",
        ["Нет подтверждённой связи"]="Connection not verified",
        ["Мониторинг активен"]="Monitoring active",
        ["Мониторинг · ошибка связи"]="Monitoring · connection error",
        ["Мониторинг остановлен"]="Monitoring stopped",
        ["Ошибка чтения"]="Read error",
    };
    private void SetStatusText(string russian)
    {
        StatusText.Text = LanguageBox?.SelectedItem is ComboBoxItem { Tag: "en" } && EnglishStatusText.TryGetValue(russian, out var english)
            ? english : russian;
    }

    private static readonly Dictionary<string, string> EnglishRuntimeMessages = new(StringComparer.Ordinal)
    {
        ["Введите корректный IPv4-адрес для диагностики"]="Enter a valid IPv4 address for diagnostics",
        ["Профиль сохранён: "]="Profile saved: ",
        ["Профиль загружен: "]="Profile loaded: ",
        ["Не удалось сохранить профиль: "]="Could not save profile: ",
        ["Не удалось загрузить профиль: "]="Could not load profile: ",
        ["COM-порты: "]="COM ports: ",
        ["Автопоиск: "]="Auto discovery: ",
        ["Тестовый TCP симулятор остановлен."]="Test TCP simulator stopped.",
        ["Тестовый Modbus TCP сервер: 127.0.0.1:1502. Для подключения выберите TCP, Host 127.0.0.1, Port 1502."]=
            "Test Modbus TCP server: 127.0.0.1:1502. Select TCP, host 127.0.0.1 and port 1502 to connect.",
        ["Modbus Scanner запущен. Запись по умолчанию требует подтверждения."]=
            "Modbus Scanner started. Writing requires confirmation by default.",
        ["Последние настройки подключения восстановлены (подключение не запускается автоматически)."]=
            "Previous connection settings restored (no automatic connection).",
        ["Сначала остановите операцию."]="Stop the current operation first.",
        ["Сначала остановите текущую операцию."]="Stop the current operation first.",
        ["Мониторинг остановлен."]="Monitoring stopped.",
        ["Автопоиск RTU завершён."]="RTU auto discovery completed.",
        ["Поиск регистров остановлен."]="Register scan stopped.",
        ["Выберите RTU."]="Select RTU.",
        ["Диагностика уже выполняется."]="A diagnostic test is already running.",
        ["Отчёт диагностики пуст."]="The diagnostic report is empty.",
        ["Не удалось восстановить настройки: "]="Could not restore settings: ",
        ["Не удалось сохранить настройки: "]="Could not save settings: ",
        ["Ошибка экспорта карты: "]="Register map export error: ",
        ["Ошибка импорта карты: "]="Register map import error: ",
        ["Ошибка чтения: "]="Read error: ",
        ["Ошибка записи FC06: "]="FC06 write error: ",
        ["Ошибка записи FC16: "]="FC16 write error: ",
        ["Ошибка записи FC05: "]="FC05 write error: ",
        ["Ошибка записи 32-бит: "]="32-bit write error: ",
        ["Ошибка поиска регистров: "]="Register scan error: ",
        ["Подключение/проверка: "]="Connection/test: ",
        ["Повторное чтение: "]="Read-back: ",
        ["Симулятор TCP: "]="TCP simulator: "
    };

    private string TranslateRuntime(string value)
    {
        if (LanguageBox?.SelectedItem is not ComboBoxItem { Tag: "en" }) return value;
        if (EnglishRuntimeMessages.TryGetValue(value, out var translated)) return translated;
        foreach (var pair in EnglishRuntimeMessages)
            if (value.StartsWith(pair.Key, StringComparison.Ordinal))
                return pair.Value + value[pair.Key.Length..];
        return value;
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || LanguageBox.SelectedItem is not ComboBoxItem selected) return;
        string language = selected.Tag as string == "en" ? "en" : "ru";
        ApplyLanguage(language);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LanguagePath)!);
            File.WriteAllText(LanguagePath, language);
        }
        catch (Exception ex) { Log("Language settings: " + ex.Message); }
    }

    private void InitializeLanguage()
    {
        CaptureLabels(this);
        string language = "ru";
        try
        {
            if (File.Exists(LanguagePath) && File.ReadAllText(LanguagePath).Trim() == "en")
                language = "en";
        }
        catch (Exception ex) { Log("Language settings: " + ex.Message); }
        LanguageBox.SelectedIndex = language == "en" ? 1 : 0;
        ApplyLanguage(language);
    }

    private sealed record LocalSettings(int Protocol, string Host, string Port, string Unit, int Function, string Address, string Quantity, string LanStart, string LanEnd);
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BloodSky", "ModbusScanner", "settings.json");

    private void RestoreLocalSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var settings = JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(SettingsPath));
            if (settings is null || settings.Protocol is < 0 or > 1 || settings.Function is < 0 or > 3) return;
            ProtocolBox.SelectedIndex = settings.Protocol;
            HostBox.Text = settings.Host;
            PortBox.Text = settings.Port;
            UnitBox.Text = settings.Unit;
            FunctionBox.SelectedIndex = settings.Function;
            StartAddressBox.Text = settings.Address;
            QuantityBox.Text = settings.Quantity;
            LanStartBox.Text = settings.LanStart;
            LanEndBox.Text = settings.LanEnd;
            Log("Последние настройки подключения восстановлены (подключение не запускается автоматически).");
        }
        catch (Exception ex) { Log("Не удалось восстановить настройки: " + ex.Message); }
    }

    private void SaveLocalSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var settings = new LocalSettings(ProtocolBox.SelectedIndex, HostBox.Text, PortBox.Text,
                UnitBox.Text, FunctionBox.SelectedIndex, StartAddressBox.Text, QuantityBox.Text,
                LanStartBox.Text, LanEndBox.Text);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log("Не удалось сохранить настройки: " + ex.Message); }
    }

    private sealed record RegisterMapEntry(ushort Address, byte Function, string Name, string Unit, double Scale, double Offset, string DataType = "UInt16", bool Writable = false);
    private sealed record RegisterMapDocument(int Version, List<RegisterMapEntry> Registers);

    private void ExportRegisterMap_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog { Title = "Экспорт карты регистров", Filter = "JSON (*.json)|*.json", DefaultExt = ".json", FileName = "register-map.json" };
            if (dialog.ShowDialog() != true) return;
            var entries = _rows.Select(r => new RegisterMapEntry(r.Address, r.Function, r.Description, r.Unit, r.Scale, r.Offset, r.DataType, r.Writable)).ToList();
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new RegisterMapDocument(1, entries), new JsonSerializerOptions { WriteIndented = true }));
            Log($"Экспорт карты: {entries.Count} регистров, {dialog.FileName}");
        }
        catch (Exception ex) { Log("Ошибка экспорта карты: " + ex.Message); }
    }

    private void ImportRegisterMap_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_scanCts != null || _operationGate.CurrentCount == 0)
                throw new InvalidOperationException("Остановите сканирование и обмен перед импортом.");
            var dialog = new OpenFileDialog { Title = "Импорт карты регистров", Filter = "JSON (*.json)|*.json" };
            if (dialog.ShowDialog() != true) return;
            if (new FileInfo(dialog.FileName).Length > 8_000_000)
                throw new InvalidDataException("Карта регистров слишком большая.");
            var document = JsonSerializer.Deserialize<RegisterMapDocument>(File.ReadAllText(dialog.FileName))
                ?? throw new InvalidDataException("Пустой или неверный файл карты.");
            if (document.Version != 1 || document.Registers is null || document.Registers.Count > 65536 ||
                document.Registers.Any(r => r.Function is < 1 or > 4 || !double.IsFinite(r.Scale) || !double.IsFinite(r.Offset) ||
                    r.DataType is not ("UInt16" or "Int16" or "UInt32" or "Int32" or "Float32") ||
                    (r.Writable && r.Function != 3)) ||
                document.Registers.Select(r => (r.Function, r.Address)).Distinct().Count() != document.Registers.Count)
                throw new InvalidDataException("Неверная версия карты или определения регистров.");
            if (MessageBox.Show(UiText($"Заменить текущую карту на {document.Registers.Count} записей? Параметры подключения останутся прежними.", $"Replace the current map with {document.Registers.Count} entries? Connection settings will remain unchanged."),
                UiText("Импорт карты", "Import map"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _rows.Clear();
            foreach (var r in document.Registers)
                _rows.Add(new RegisterRow { Address = r.Address, Function = r.Function, Description = r.Name, Unit = r.Unit, Scale = r.Scale, Offset = r.Offset, DataType = r.DataType, Writable = r.Writable });
            Log($"Импортировано {document.Registers.Count} регистров из {dialog.FileName}.");
        }
        catch (Exception ex) { Log("Ошибка импорта карты: " + ex.Message); }
    }

    private sealed record ScannerProfile(
        int ProtocolIndex, string Host, string Port, string? ComPort, int BaudIndex,
        int ParityIndex, int DataBitsIndex, int StopBitsIndex, string UnitId,
        int FunctionIndex, string StartAddress, string Quantity, int ValueTypeIndex, string MonitorInterval, List<RegisterDefinition>? Registers = null, int? BaudRate = null);

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Сохранить профиль Modbus", Filter = "Профиль Modbus (*.json)|*.json", DefaultExt = ".json", FileName = "modbus-profile.json" };
            if (dialog.ShowDialog() != true) return;
            var profile = new ScannerProfile(ProtocolBox.SelectedIndex, HostBox.Text, PortBox.Text, ComBox.SelectedItem?.ToString(),
                BaudBox.SelectedIndex, ParityBox.SelectedIndex, DataBitsBox.SelectedIndex, StopBitsBox.SelectedIndex,
                UnitBox.Text, FunctionBox.SelectedIndex, StartAddressBox.Text, QuantityBox.Text, ValueTypeBox.SelectedIndex, MonitorIntervalBox.Text, _rows.Select(r => new RegisterDefinition(r.Address, r.Function, r.Description, r.Unit, r.Scale, r.Offset)).ToList(), int.Parse(((ComboBoxItem)BaudBox.SelectedItem).Content.ToString()!, CultureInfo.InvariantCulture));
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
            Log("Профиль сохранён: " + dialog.FileName);
        }
        catch (Exception ex) { Log("Не удалось сохранить профиль: " + ex.Message); }
    }

    private void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Загрузить профиль Modbus", Filter = "Профиль Modbus (*.json)|*.json|Все файлы (*.*)|*.*", DefaultExt = ".json" };
            if (dialog.ShowDialog() != true) return;
            if(new FileInfo(dialog.FileName).Length>8_000_000)throw new InvalidDataException("Профиль слишком большой.");
            var profile = JsonSerializer.Deserialize<ScannerProfile>(File.ReadAllText(dialog.FileName))
                ?? throw new InvalidDataException("Файл профиля пуст или имеет неверный формат.");
            if (profile.ProtocolIndex is < 0 or > 1 || profile.FunctionIndex is < 0 or > 3 || profile.ValueTypeIndex is < 0 or > 9 ||
                (profile.BaudRate == null && (profile.BaudIndex < 0 || profile.BaudIndex >= BaudBox.Items.Count)) || profile.ParityIndex < 0 || profile.ParityIndex >= ParityBox.Items.Count ||
                profile.DataBitsIndex < 0 || profile.DataBitsIndex >= DataBitsBox.Items.Count || profile.StopBitsIndex < 0 || profile.StopBitsIndex >= StopBitsBox.Items.Count)
                throw new InvalidDataException("В профиле содержатся недопустимые индексы настроек.");
            if (_scanCts != null || _operationGate.CurrentCount==0) throw new InvalidOperationException("Сначала остановите текущую операцию.");
            var definitions = profile.Registers ?? new();
            if (definitions.Any(r => r.Function is < 1 or > 4 || !double.IsFinite(r.Scale) || !double.IsFinite(r.Offset)) || definitions.Select(r => (r.Function,r.Address)).Distinct().Count() != definitions.Count)
                throw new InvalidDataException("Некорректные определения регистров.");
            if(profile.BaudRate.HasValue && (profile.BaudRate.Value<300 || profile.BaudRate.Value>1000000))throw new InvalidDataException("Некорректная скорость.");
            Disconnect();
            _rows.Clear();
            foreach (var d in definitions) _rows.Add(new RegisterRow { Address=d.Address, Function=d.Function, Description=d.Name, Unit=d.Unit, Scale=d.Scale, Offset=d.Offset });
            ProtocolBox.SelectedIndex = profile.ProtocolIndex;
            HostBox.Text = profile.Host ?? "";
            PortBox.Text = profile.Port ?? "502";
            if (!string.IsNullOrWhiteSpace(profile.ComPort) && ComBox.Items.Contains(profile.ComPort)) ComBox.SelectedItem = profile.ComPort;
            if(profile.BaudRate.HasValue){
                if(profile.BaudRate.Value<300 || profile.BaudRate.Value>1000000)throw new InvalidDataException("Некорректная скорость.");
                var baudItem=BaudBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>x.Content.ToString()==profile.BaudRate.Value.ToString());
                if(baudItem==null){baudItem=new ComboBoxItem{Content=profile.BaudRate.Value.ToString()};BaudBox.Items.Add(baudItem);}BaudBox.SelectedItem=baudItem;
            }else BaudBox.SelectedIndex = profile.BaudIndex;
            ParityBox.SelectedIndex = profile.ParityIndex;
            DataBitsBox.SelectedIndex = profile.DataBitsIndex; StopBitsBox.SelectedIndex = profile.StopBitsIndex;
            UnitBox.Text = profile.UnitId ?? "1"; FunctionBox.SelectedIndex = profile.FunctionIndex;
            StartAddressBox.Text = profile.StartAddress ?? "0"; QuantityBox.Text = profile.Quantity ?? "100";
            ValueTypeBox.SelectedIndex = profile.ValueTypeIndex; MonitorIntervalBox.Text = profile.MonitorInterval ?? "1000";
            Log("Профиль загружен: " + dialog.FileName);
            SetStatusText("Профиль загружен · подключение не проверено");
        }
        catch (Exception ex) { Log("Не удалось загрузить профиль: " + ex.Message); }
    }

    private void ValueTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInterpretedValues();

    private string FormatWriteValue(ushort value) =>
        WriteValueFormatBox?.SelectedIndex == 1
            ? $"0x{value:X4}"
            : value.ToString(CultureInfo.InvariantCulture);

    private void SetBit_Click(object sender, RoutedEventArgs e) => ChangePreparedBit(true);
    private void ClearBit_Click(object sender, RoutedEventArgs e) => ChangePreparedBit(false);

    private void ChangePreparedBit(bool set)
    {
        try
        {
            if (BitIndexBox.SelectedIndex is < 0 or > 15)
                throw new InvalidOperationException("Выберите бит 0–15.");
            ushort before = ParseWriteValue();
            ushort mask = (ushort)(1 << BitIndexBox.SelectedIndex);
            ushort after = set ? (ushort)(before | mask) : (ushort)(before & ~mask);
            WriteValueBox.Text = FormatWriteValue(after);
            Log($"Подготовка бита {BitIndexBox.SelectedIndex}: 0x{before:X4} → 0x{after:X4}. На устройство ничего не отправлено.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, UiText("Редактор битов", "Bit editor"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private ushort ParseWriteValue()
    {
        var input = WriteValueBox.Text.Trim();
        if (WriteValueFormatBox.SelectedIndex == 1)
        {
            if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) input = input[2..];
            if (ushort.TryParse(input, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
                return hex;
            throw new FormatException("HEX: введите 0000–FFFF (префикс 0x необязателен).");
        }
        return ParseUShort(input, "Значение");
    }

    private void WriteValueFormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WriteValueBox is null || WriteValueFormatBox is null) return;
        var input = WriteValueBox.Text.Trim();
        if (input.Length == 0) return;
        var previousHex = e.RemovedItems.Count > 0 &&
            e.RemovedItems[0] is ComboBoxItem previous &&
            string.Equals(previous.Content?.ToString(), "HEX", StringComparison.OrdinalIgnoreCase);
        if (previousHex && input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            input = input[2..];
        bool valid = previousHex
            ? ushort.TryParse(input, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)
            : ushort.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        if (!valid)
        {
            MessageBox.Show(UiText("Исправьте значение 0–65535 / 0000–FFFF перед сменой формата.", "Enter a valid value (0–65535 / 0000–FFFF) before changing format."),
                UiText("Формат записи", "Write format"), MessageBoxButton.OK, MessageBoxImage.Warning);
            WriteValueFormatBox.SelectionChanged -= WriteValueFormatBox_SelectionChanged;
            WriteValueFormatBox.SelectedIndex = previousHex ? 1 : 0;
            WriteValueFormatBox.SelectionChanged += WriteValueFormatBox_SelectionChanged;
            return;
        }
        ushort value = previousHex
            ? ushort.Parse(input, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : ushort.Parse(input, NumberStyles.Integer, CultureInfo.InvariantCulture);
        WriteValueBox.Text = FormatWriteValue(value);
    }

    private void RegistersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RegistersGrid.SelectedItem is not RegisterRow row || WriteAddressBox is null || WriteValueBox is null) return;
        WriteAddressBox.Text = row.Address.ToString(CultureInfo.InvariantCulture);
        WriteValueBox.Text = FormatWriteValue(row.RawValue);
    }

    private void UpdateInterpretedValues()
    {
        if (ValueTypeBox is null || RegistersGrid is null) return;
        var byAddress = _rows.Where(r => r.Function == FunctionBox.SelectedIndex+1).GroupBy(r => (int)r.Address).ToDictionary(g => g.Key, g => g.First().RawValue);
        foreach (var row in _rows)
        {
            if (row.Function != FunctionBox.SelectedIndex+1) continue;
            row.SetNext(byAddress.TryGetValue(row.Address+1,out var pairValue)?pairValue:null);
            uint bits = row.RawValue;
            bool needsPair = ValueMode >= 2;
            if (needsPair)
            {
                if (!byAddress.TryGetValue(row.Address + 1, out ushort next)) { row.DisplayValue = "— (нужен следующий регистр)"; continue; }
                bits = ValueMode is 5 or 6 or 7 ? ((uint)next << 16) | row.RawValue : ((uint)row.RawValue << 16) | next;
            }
            if (ValueMode == 8) bits = RegisterAnalysis.Order32(row.RawValue, byAddress[row.Address+1], "BADC");
            if (ValueMode == 9) bits = RegisterAnalysis.Order32(row.RawValue, byAddress[row.Address+1], "DCBA");
            row.DisplayValue = ValueMode switch
            {
                1 => unchecked((short)row.RawValue).ToString(CultureInfo.InvariantCulture),
                2 or 5 => bits.ToString(CultureInfo.InvariantCulture),
                3 or 6 => unchecked((int)bits).ToString(CultureInfo.InvariantCulture),
                4 or 7 or 8 or 9 => BitConverter.Int32BitsToSingle(unchecked((int)bits)).ToString("G9", CultureInfo.InvariantCulture),
                _ => row.RawValue.ToString(CultureInfo.InvariantCulture)
            };
        }
    }

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComBox is null) return;
        Disconnect();
        bool tcp = IsTcp;
        ComBox.IsEnabled = !tcp;
        BaudBox.IsEnabled = !tcp;
        ParityBox.IsEnabled = !tcp;
        DataBitsBox.IsEnabled = !tcp;
        StopBitsBox.IsEnabled = !tcp;
        HostBox.IsEnabled = tcp;
        PortBox.IsEnabled = tcp;
        StatusText.Text = tcp ? UiText("Режим Modbus TCP", "Modbus TCP mode") : UiText("Режим Modbus RTU", "Modbus RTU mode");
    }

    private void RefreshCom_Click(object sender, RoutedEventArgs e) { if(_scanCts==null && _operationGate.CurrentCount==1)RefreshComPorts(); }

    private void RefreshComPorts()
    {
        var old = ComBox.SelectedItem?.ToString();
        ComBox.ItemsSource = SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (old is not null && ComBox.Items.Contains(old)) ComBox.SelectedItem = old;
        else if (ComBox.Items.Count > 0) ComBox.SelectedIndex = 0;
        Log("COM-порты: " + (ComBox.Items.Count == 0 ? "не найдены" : string.Join(", ", ComBox.Items.Cast<string>())));
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if(_scanCts!=null || _operationGate.CurrentCount==0){Log("Сначала остановите операцию.");return;}
        try
        {
            if (_connected)
            {
                Disconnect();
                SetStatusText("Нет подтверждённой связи");
                StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                Log(UiText("Отключено пользователем.", "Disconnected by user."));
                return;
            }
            var unit = GetUnit();
            var fc = (byte)(FunctionBox.SelectedIndex + 1);
            var values = await ReadAsync(unit, fc, GetStart(), 1, CancellationToken.None);
            _connected = true;
            StatusText.Text = UiText($"Связь подтверждена · Unit {unit}", $"Connection verified · Unit {unit}");
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            Log($"Связь OK: Unit={unit}, FC={fc}, ответ получен.");
            if (values.Length > 0) WriteValueBox.Text = FormatWriteValue(values[0]);
        }
        catch (Exception ex)
        {
            SetStatusText("Нет подтверждённой связи");
            StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            Log("Подключение/проверка: " + ExplainConnectionError(ex));
            Disconnect();
        }
    }

    private string ExplainConnectionError(Exception ex)
    {
        if (ex is TimeoutException || ex is OperationCanceledException)
            return UiText("Устройство не ответило вовремя. Проверьте IP/порт или COM, питание, Unit ID, параметры линии и доступность сети. Подробности: ", "Device response timed out. Check IP/port or COM, power, Unit ID, line settings and network reachability. Details: ") + ex.Message;
        if (ex is SocketException socket)
            return UiText("Не удалось установить TCP-соединение. Проверьте IP-адрес, порт, сеть и настройки брандмауэра. Подробности: ", "TCP connection failed. Check IP address, port, network and firewall settings. Details: ") + socket.Message;
        if (ex is UnauthorizedAccessException)
            return UiText("Нет доступа к COM-порту. Возможно, порт занят другой программой. Подробности: ", "COM port access denied. Another application may be using the port. Details: ") + ex.Message;
        if (ex is IOException)
            return UiText("Ошибка связи с устройством. Проверьте кабель, соединение и настройки порта. Подробности: ", "Device communication error. Check cabling, connection and port settings. Details: ") + ex.Message;
        return ex.Message;
    }

    private async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        if(_transportContext!=ConnectionContext){Disconnect();_transportContext=ConnectionContext;}
        if (IsTcp)
        {
            if (_tcp?.Connected == true && _stream is not null) return;
            Disconnect();
            int port = ParseInt(PortBox.Text, 1, 65535, "TCP-порт");
            _tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 2000, SendTimeout = 2000 };
            await _tcp.ConnectAsync(HostBox.Text.Trim(), port, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
            _stream = _tcp.GetStream();
            _stream.ReadTimeout = 2000;
            _stream.WriteTimeout = 2000;
            Log($"TCP подключён: {HostBox.Text.Trim()}:{port}");
        }
        else
        {
            if (_serial?.IsOpen == true) return;
            Disconnect();
            var com = ComBox.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(com)) throw new InvalidOperationException("Выберите COM-порт.");
            int baud = int.Parse(((ComboBoxItem)BaudBox.SelectedItem).Content.ToString()!, CultureInfo.InvariantCulture);
            int dataBits = int.Parse(((ComboBoxItem)DataBitsBox.SelectedItem).Content.ToString()!, CultureInfo.InvariantCulture);
            var parity = ParityBox.SelectedIndex switch { 1 => Parity.Even, 2 => Parity.Odd, _ => Parity.None };
            var stop = StopBitsBox.SelectedIndex == 1 ? StopBits.Two : StopBits.One;
            _serial = new SerialPort(com, baud, parity, dataBits, stop)
            {
                ReadTimeout = 650,
                WriteTimeout = 650,
                DtrEnable = false,
                RtsEnable = false
            };
            _serial.Open();
            Log($"RTU порт открыт: {com}, {baud}, {parity}, {dataBits}{(stop == StopBits.One ? "N1" : "N2")}");
        }
    }

    private void Disconnect()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Close(); } catch { }
        try { if (_serial?.IsOpen == true) _serial.Close(); } catch { }
        _stream = null; _tcp = null; _serial = null; _connected = false;
    }


    private async void StartMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCts != null || _operationGate.CurrentCount==0) { Log("Сначала остановите текущую операцию."); return; }
        int interval;
        try { interval = ParseInt(MonitorIntervalBox.Text, 200, 60000, "Период мониторинга"); GetStart(); GetQuantity(); GetUnit(); }
        catch (Exception ex) { Log(ex.Message); return; }
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        SetBusy(true);
        var ct = cts.Token;
        Log($"Мониторинг запущен: FC{FunctionBox.SelectedIndex + 1}, адрес {GetStart()}, количество {GetQuantity()}, период {interval} мс.");
        SetStatusText("Мониторинг активен");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await EnsureConnectedAsync(ct);
                    var unit = GetUnit();
                    var function = (byte)(FunctionBox.SelectedIndex + 1);
                    var start = GetStart();
                    var values = await ReadAsync(unit, function, start, GetQuantity(), ct);
                    ApplyReadValues(start, values, function);
                    _connected = true;
                    StatusText.Text = UiText($"Мониторинг · OK · {DateTime.Now:HH:mm:ss}", $"Monitoring · OK · {DateTime.Now:HH:mm:ss}");
                    StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    SetStatusText("Мониторинг · ошибка связи");
                    StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                    Log($"Мониторинг: {ex.Message}; повторное подключение…");
                    Disconnect();
                    await Task.Delay(Math.Min(interval, 1500), ct);
                }
                int delay = interval - (int)started.ElapsedMilliseconds;
                if (delay > 0) await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            bool wasCurrentMonitor = ReferenceEquals(_scanCts, cts);
            if (wasCurrentMonitor)
            {
                _scanCts = null;
                SetBusy(false);
                SetStatusText("Мониторинг остановлен");
                StatusText.Foreground = System.Windows.Media.Brushes.LightGray;
                Log("Мониторинг остановлен.");
            }
            cts.Dispose();
        }
    }

    private void ApplyReadValues(ushort start, ushort[] values, byte function)
    {
        EnsureDataContext();
        for (int i = 0; i < values.Length; i++)
        {
            ushort address = (ushort)(start + i);
            var row = _rows.FirstOrDefault(r => r.Address == address && r.Function == function);
            if (row is null) { row = new RegisterRow { Address = address }; _rows.Add(row); }
            row.Function = function;
            row.RawValue = values[i];
            row.PendingValue = values[i];
            row.Quality = "OK";
            if (function is 1 or 2) row.Description = values[i] == 0 ? "OFF" : "ON";
        }
        SortRows();
        UpdateInterpretedValues();
        CaptureHistory(function, start, values);
        UpdateSnapshotComparison();
    }

    private async void ReadRange_Click(object sender, RoutedEventArgs e)
    {
        if(_scanCts!=null || _operationGate.CurrentCount==0){Log("Сначала остановите текущую операцию.");return;}
        try
        {
            var values = await ReadAsync(GetUnit(), (byte)(FunctionBox.SelectedIndex + 1), GetStart(), GetQuantity(), CancellationToken.None);
            ApplyReadValues(GetStart(), values, (byte)(FunctionBox.SelectedIndex + 1));
            Log($"Прочитано {values.Length} значений с адреса {GetStart()} (FC{FunctionBox.SelectedIndex + 1}).");
            StatusText.Text = UiText($"Прочитано: {values.Length}", $"Read: {values.Length}");
        }
        catch (Exception ex) { Log("Ошибка чтения: " + ex.Message); SetStatusText("Ошибка чтения"); }
    }

    private async Task<ushort[]> ReadAsync(byte unit, byte function, ushort start, ushort quantity, CancellationToken ct)
    {
        if (function is not (1 or 2 or 3 or 4))
            throw new ArgumentOutOfRangeException(nameof(function), "Поддерживаются только FC01–FC04 для чтения.");
        if (quantity == 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Количество значений должно быть больше нуля.");
        if ((uint)start + quantity > 65536)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Диапазон чтения выходит за пределы адресов Modbus 0–65535.");
        if (quantity > 125 && function is 3 or 4)
            throw new ArgumentOutOfRangeException(nameof(quantity), "FC03/04: допустимо 1–125 регистров.");
        if (quantity > 2000 && function is 1 or 2)
            throw new ArgumentOutOfRangeException(nameof(quantity), "FC01/02: допустимо 1–2000 бит.");
        byte[] request = { unit, function, (byte)(start >> 8), (byte)start, (byte)(quantity >> 8), (byte)quantity };
        var response = await ExchangeAsync(request, function, ct);
        if (response.Length < 3 || response[0] != unit) throw new IOException("Ответ Modbus имеет неверный адрес или длину.");
        if ((response[1] & 0x80) != 0) throw new IOException($"Modbus exception code {response.ElementAtOrDefault(2):X2} для FC{function}.");
        if (response[1] != function) throw new IOException("Функция в ответе не совпадает с запросом.");
        int byteCount = response[2];
        // RTU responses retain the two CRC bytes for validation; TCP responses do not include CRC.
        int expectedResponseLength = 3 + byteCount + (IsTcp ? 0 : 2);
        if (response.Length != expectedResponseLength)
            throw new IOException(response.Length < expectedResponseLength
                ? "Ответ обрезан: не хватает данных или CRC."
                : "Ответ содержит лишние байты после поля данных.");
        if (function is 1 or 2)
        {
            int expectedByteCount = (quantity + 7) / 8;
            if (byteCount != expectedByteCount)
                throw new IOException($"Неожиданная длина битовых данных: {byteCount} байт; ожидалось {expectedByteCount}.");
            var bits = new ushort[quantity];
            for (int i = 0; i < quantity; i++) bits[i] = (ushort)((response[3 + i / 8] >> (i % 8)) & 1);
            return bits;
        }
        if (byteCount != quantity * 2) throw new IOException($"Неожиданная длина данных: {byteCount} байт.");
        var result = new ushort[quantity];
        for (int i = 0; i < quantity; i++) result[i] = (ushort)((response[3 + i * 2] << 8) | response[4 + i * 2]);
        return result;
    }

    private async Task<byte[]> ExchangeAsync(byte[] pdu, byte function, CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct);
        SetBusy(true);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Recheck inside the serialized exchange: a scan may start while a write waits for the gate.
            if (function is 5 or 6 or 16 && _scanCts is not null)
                throw new InvalidOperationException(UiText("Остановите мониторинг или поиск перед записью в устройство.", "Stop monitoring or scanning before writing to the device."));
            LogFrame("TX PDU", pdu);
            var result = await ExchangeCoreAsync(pdu, function, ct);
            LogFrame("RX ADU (RTU) / Unit+PDU (TCP)", result);
            if (result.Length < 2)
                throw new IOException(UiText("Устройство вернуло слишком короткий ответ Modbus. Проверьте соединение и настройки устройства.", "The device returned an incomplete Modbus response. Check the connection and device settings."));
            if ((result[1] & 0x80) != 0)
            {
                if (result.Length != (IsTcp ? 3 : 5)) throw new IOException("Неверная длина исключения Modbus.");
                throw new ModbusDeviceException(result[2]);
            }
            FrameChecks.Validate(pdu, result, IsTcp);
            RecordExchange(timer.Elapsed.TotalMilliseconds, null);
            return result;
        }
        catch (Exception ex)
        {
            RecordExchange(timer.Elapsed.TotalMilliseconds, ex);
            if (ex is not ModbusDeviceException && ex is not InvalidOperationException) Disconnect();
            throw;
        }
        finally { if(_scanCts==null)SetBusy(false); _operationGate.Release(); }
    }

    private async Task<byte[]> ExchangeCoreAsync(byte[] pdu, byte function, CancellationToken ct)
    {
        EnsureDataContext();
        await EnsureConnectedAsync(ct);
        if (IsTcp)
        {
            // A TCP stream has no message boundaries: serialize complete request/response
            // exchanges so monitoring and manual reads cannot consume each other's replies.
            await _tcpGate.WaitAsync(ct);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(2000);
                ct = deadline.Token;
                var stream = _stream ?? throw new IOException("TCP-поток не открыт.");
                ushort tid = unchecked(++_transactionId);
                // MBAP is 6 bytes, followed by Unit ID and the Modbus PDU.
                byte[] frame = new byte[6 + pdu.Length];
                frame[0] = (byte)(tid >> 8); frame[1] = (byte)tid;
                frame[2] = 0; frame[3] = 0;
                ushort len = (ushort)pdu.Length;
                frame[4] = (byte)(len >> 8); frame[5] = (byte)len; frame[6] = pdu[0];
                Array.Copy(pdu, 1, frame, 7, pdu.Length - 1);
                LogFrame("TX TCP ADU",frame);
                await stream.WriteAsync(frame, ct);
                byte[] header = new byte[7];
                await ReadExactAsync(stream, header, ct);
                int length = (header[4] << 8) | header[5];
                if (header[0] != frame[0] || header[1] != frame[1] || header[2] != 0 || header[3] != 0 || length < 2 || length > 254)
                    throw new IOException("Некорректный MBAP заголовок.");
                if (header[6] != pdu[0])
                    throw new IOException($"Ответ пришёл от Unit ID {header[6]}, ожидался {pdu[0]}.");
                byte[] response = new byte[length];
                response[0] = header[6];
                // Read function and data after the Unit ID.
                byte[] tail = new byte[length - 1];
                await ReadExactAsync(stream, tail, ct);
                Array.Copy(tail, 0, response, 1, tail.Length);
                LogFrame("RX TCP ADU",header.Concat(tail).ToArray());
                if (response.Length < 2 || (response[1] != function && response[1] != (byte)(function | 0x80)))
                    throw new IOException("Функция в ответе Modbus TCP не совпадает с запросом.");
                return response;
            }
            finally { _tcpGate.Release(); }
        }

        return await Task.Run(() =>
        {
            lock (_serialGate)
            {
                var serial = _serial ?? throw new IOException("COM-порт не открыт.");
                serial.DiscardInBuffer();
                byte[] frame = new byte[pdu.Length + 2];
                Array.Copy(pdu, frame, pdu.Length);
                ushort crc = Crc16(frame, pdu.Length);
                frame[^2] = (byte)crc; frame[^1] = (byte)(crc >> 8);
                ct.ThrowIfCancellationRequested();
                var serialTimer=System.Diagnostics.Stopwatch.StartNew();
                int serialBudget=650;
                int NextByte(){ ct.ThrowIfCancellationRequested(); int remaining=serialBudget-(int)serialTimer.ElapsedMilliseconds; if(remaining<=0)throw new TimeoutException("Тайм-аут RTU кадра."); serial.ReadTimeout=remaining;return ReadSerialByte(serial); }
                LogFrame("TX RTU ADU",frame);
                serial.Write(frame, 0, frame.Length);
                var first = NextByte();
                if (first != pdu[0]) throw new IOException("Ответ от другого Slave ID.");
                var second = NextByte();
                if ((second & 0x80) != 0)
                {
                    var exception = NextByte();
                    int crcLow = NextByte();
                    int crcHigh = NextByte();
                    byte[] exceptionFrame = { (byte)first, (byte)second, (byte)exception };
                    ushort expectedExceptionCrc = Crc16(exceptionFrame, exceptionFrame.Length);
                    ushort receivedExceptionCrc = (ushort)(crcLow | (crcHigh << 8));
                    if (expectedExceptionCrc != receivedExceptionCrc)
                        throw new IOException("CRC16 исключения RTU не совпадает.");
                    if (second != (function | 0x80)) throw new IOException("Неверная функция исключения RTU.");
                    return new byte[] { (byte)first, (byte)second, (byte)exception, (byte)crcLow, (byte)crcHigh };
                }
                if (second != function) throw new IOException("Функция ответа не совпадает.");
                byte[] answer;
                if (function is 5 or 6 or 16)
                {
                    // FC05/06/16 replies echo the address/value or address/quantity; there is no byte-count field.
                    answer = new byte[8];
                    answer[0] = (byte)first; answer[1] = (byte)second;
                    for (int i = 2; i < answer.Length; i++) answer[i] = (byte)NextByte();
                }
                else
                {
                    var byteCount = NextByte();
                    if (byteCount > 250)
                        throw new IOException($"Недопустимая длина данных RTU: {byteCount} байт (максимум 250).");
                    if (function is 3 or 4 && (byteCount == 0 || (byteCount & 1) != 0))
                        throw new IOException($"Недопустимая длина ответа RTU для FC{function}: {byteCount} байт.");
                    byte[] body = new byte[byteCount + 2];
                    for (int i = 0; i < body.Length; i++) body[i] = (byte)NextByte();
                    answer = new byte[3 + body.Length];
                    answer[0] = (byte)first; answer[1] = (byte)second; answer[2] = (byte)byteCount;
                    Array.Copy(body, 0, answer, 3, body.Length);
                }
                ushort expected = Crc16(answer, answer.Length - 2);
                ushort received = (ushort)(answer[^2] | (answer[^1] << 8));
                if (expected != received) throw new IOException("CRC16 ответа не совпадает.");
                return answer;
            }
        }, ct);
    }

    private static int ReadSerialByte(SerialPort serial)
    {
        int value = serial.ReadByte();
        if (value < 0) throw new EndOfStreamException("Ожидался ответ RTU.");
        return value;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (n == 0) throw new EndOfStreamException("TCP-соединение закрыто во время ответа.");
            offset += n;
        }
    }

    private static ushort Crc16(byte[] data, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < length; i++)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++) crc = (ushort)(((crc & 1) != 0) ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        return crc;
    }

    private async void WriteRegister_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ushort address = ParseUShort(WriteAddressBox.Text, "Адрес записи");
            ushort value = ParseWriteValue();
            if (!ApproveWrite(6, address, new[] { value })) return;
            await WriteAsync(GetUnit(), 6, address, new[] { value }, CancellationToken.None);
            Log($"FC06: записан регистр {address} = {value} (0x{value:X4}).");
            await ReadBackAsync(address, 1, expected: new[] { value });
        }
        catch (Exception ex) { Log("Ошибка записи FC06: " + ex.Message); }
    }

    private async void Write32_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ushort address = ParseUShort(WriteAddressBox.Text, "Адрес 32-битного значения");
            if (address == ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(address), "Нужно два последовательных регистра.");
            if (RegistersGrid.SelectedItem is not RegisterRow row || row.Function != 3 || row.Address != address)
                throw new InvalidOperationException("Выберите первый Holding Register FC03 с указанным адресом.");
            if (!_rows.Any(r => r.Function == 3 && r.Address == address + 1))
                throw new InvalidOperationException("Сначала прочитайте оба соседних Holding Registers.");
            uint bits = Write32TypeBox.SelectedIndex switch
            {
                0 => BitConverter.SingleToUInt32Bits(float.Parse(Write32ValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture)),
                1 => unchecked((uint)int.Parse(Write32ValueBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                2 => uint.Parse(Write32ValueBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException("Выберите тип 32-битного значения.")
            };
            if (Write32TypeBox.SelectedIndex == 0 && !float.IsFinite(BitConverter.UInt32BitsToSingle(bits)))
                throw new FormatException("NaN и Infinity запрещены для записи Float32.");
            byte a = (byte)(bits >> 24), b = (byte)(bits >> 16), cc = (byte)(bits >> 8), d = (byte)bits;
            byte[] bytes = Write32OrderBox.SelectedIndex switch
            {
                0 => new[] { a, b, cc, d },
                1 => new[] { b, a, d, cc },
                2 => new[] { cc, d, a, b },
                3 => new[] { d, cc, b, a },
                _ => throw new InvalidOperationException("Выберите порядок байтов.")
            };
            ushort[] values = { (ushort)((bytes[0] << 8) | bytes[1]), (ushort)((bytes[2] << 8) | bytes[3]) };
            if (!ApproveWrite(16, address, values)) return;
            await WriteAsync(GetUnit(), 16, address, values, CancellationToken.None);
            Log($"FC16: 32-бит {Write32TypeBox.Text} {Write32ValueBox.Text}, порядок {Write32OrderBox.Text}, адреса {address}–{address + 1}, значения 0x{values[0]:X4} 0x{values[1]:X4}.");
            await ReadBackAsync(address, 2, expected: values);
        }
        catch (Exception ex) { Log("Ошибка записи 32-бит: " + ex.Message); }
    }

    private async void WriteMultiple_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RegistersGrid.SelectedItem is not RegisterRow row || row.Function != 3) throw new InvalidOperationException("Выберите строку Holding Registers FC03.");
            RegistersGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            RegistersGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var selected = _rows.Where(r => r.Function == 3 && r.Address >= row.Address).OrderBy(r => r.Address).ToList();
            var contiguous = new List<RegisterRow> { selected[0] };
            for (int i = 1; i < selected.Count && contiguous.Count < 123; i++)
            {
                if (selected[i].Address != contiguous[^1].Address + 1) break;
                contiguous.Add(selected[i]);
            }
            var values = contiguous.Select(r => r.PendingValue).ToArray();
            if (!ApproveWrite(16, row.Address, values)) return;
            await WriteAsync(GetUnit(), 16, row.Address, values, CancellationToken.None);
            Log($"FC16: записано {values.Length} регистров с адреса {row.Address}.");
            await ReadBackAsync(row.Address, (ushort)values.Length, expected: values);
        }
        catch (Exception ex) { Log("Ошибка записи FC16: " + ex.Message); }
    }

    private async void WriteCoil_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ushort address = ParseUShort(WriteAddressBox.Text, "Адрес Coil");
            ushort coilValue = ParseWriteValue();
            if (coilValue is not (0 or 1))
                throw new ArgumentOutOfRangeException(nameof(WriteValueBox), "Для Coil укажите 0 (OFF) или 1 (ON).");
            bool on = coilValue == 1;
            if (!ApproveWrite(5, address, new[] { coilValue })) return;
            await WriteAsync(GetUnit(), 5, address, new[] { (ushort)(on ? 1 : 0) }, CancellationToken.None);
            Log($"FC05: Coil {address} = {(on ? "ON" : "OFF")}.");
            await ReadBackAsync(address, 1, 1, new[] { coilValue });
        }
        catch (Exception ex) { Log("Ошибка записи FC05: " + ex.Message); }
    }

    private async Task WriteAsync(byte unit, byte function, ushort address, ushort[] values, CancellationToken ct)
    {
        if (_scanCts is not null)
            throw new InvalidOperationException(UiText("Остановите мониторинг или поиск перед записью в устройство.", "Stop monitoring or scanning before writing to the device."));
        if (function is not (5 or 6 or 16))
            throw new ArgumentOutOfRangeException(nameof(function), "Поддерживаются только FC05, FC06 и FC16 для записи.");
        if (values is null || values.Length == 0)
            throw new ArgumentException("Нужно указать хотя бы одно значение для записи.", nameof(values));
        if ((function is 5 or 6) && values.Length != 1)
            throw new ArgumentException("FC05 и FC06 записывают ровно одно значение.", nameof(values));
        if (function == 5 && values[0] is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(values), "Для FC05 значение катушки должно быть 0 или 1.");
        if (function == 16 && values.Length > 123)
            throw new ArgumentOutOfRangeException(nameof(values), "FC16 поддерживает не более 123 регистров за запрос.");
        int addressCount = function == 16 ? values.Length : 1;
        if ((uint)address + (uint)addressCount > 65536)
            throw new ArgumentOutOfRangeException(nameof(address), "Диапазон записи выходит за пределы адресов Modbus 0–65535.");
        byte[] request;
        if (function == 6 || function == 5)
        {
            ushort value = function == 5 ? (values[0] != 0 ? (ushort)0xFF00 : (ushort)0) : values[0];
            request = new byte[] { unit, function, (byte)(address >> 8), (byte)address, (byte)(value >> 8), (byte)value };
        }
        else
        {
            if (values.Length is < 1 or > 123) throw new ArgumentOutOfRangeException(nameof(values), "FC16 поддерживает 1–123 регистров.");
            request = new byte[7 + values.Length * 2];
            request[0] = unit; request[1] = 16; request[2] = (byte)(address >> 8); request[3] = (byte)address;
            request[4] = (byte)(values.Length >> 8); request[5] = (byte)values.Length; request[6] = (byte)(values.Length * 2);
            for (int i = 0; i < values.Length; i++) { request[7 + i * 2] = (byte)(values[i] >> 8); request[8 + i * 2] = (byte)values[i]; }
        }
        var response = await ExchangeAsync(request, function, ct);
        if (response.Length < 2 || response[0] != unit) throw new IOException("Некорректное подтверждение записи: неверный Unit ID или слишком короткий ответ.");
        // A Modbus TCP exception reply is only 3 bytes (Unit ID, function, exception code),
        // so inspect it before enforcing the normal write-echo length.
        if ((response[1] & 0x80) != 0) throw new IOException($"Устройство отклонило запись: exception {response.ElementAtOrDefault(2):X2}.");
        if (response[1] != function) throw new IOException("Неверная функция в подтверждении записи.");
        int expectedResponseLength = IsTcp ? 6 : 8; // RTU includes two CRC bytes.
        if (response.Length != expectedResponseLength) throw new IOException("Неверная длина подтверждения записи.");
        ushort echoedAddress = (ushort)((response[2] << 8) | response[3]);
        ushort echoedValueOrCount = (ushort)((response[4] << 8) | response[5]);
        ushort expectedEcho = function == 16 ? (ushort)values.Length : function == 5 ? (values[0] != 0 ? (ushort)0xFF00 : (ushort)0) : values[0];
        if (echoedAddress != address || echoedValueOrCount != expectedEcho)
            throw new IOException($"Подтверждение записи не совпало: адрес={echoedAddress}, значение/количество={echoedValueOrCount}; ожидалось адрес={address}, значение/количество={expectedEcho}.");
    }

    private async Task ReadBackAsync(ushort address, ushort quantity, byte? function = null, ushort[]? expected = null)
    {
        byte fc = function ?? 3;
        try
        {
            var values = await ReadAsync(GetUnit(), fc, address, quantity, CancellationToken.None);
            ApplyReadValues(address, values, fc);
            if (expected is not null)
            {
                bool match = values.SequenceEqual(expected);
                string requested = string.Join(", ", expected.Select(v => $"{v} (0x{v:X4})"));
                string actual = string.Join(", ", values.Select(v => $"{v} (0x{v:X4})"));
                Log($"Проверка записи {(match ? "PASS" : "MISMATCH")}: FC{fc:D2}, адрес {address}, запрошено [{requested}], прочитано [{actual}].");
                if (!match)
                    MessageBox.Show(UiText($"Запись не подтверждена повторным чтением!\nАдрес: {address}\nЗапрошено: {requested}\nПрочитано: {actual}", $"Write verification failed!\nAddress: {address}\nRequested: {requested}\nRead back: {actual}"),
                        UiText("Modbus: несовпадение", "Modbus: mismatch"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) when (expected is not null)
        {
            Log($"Проверка записи READ ERROR: FC{fc:D2}, адрес {address}: {ex.Message}");
            MessageBox.Show(UiText($"Устройство ответило на запись, но проверить значение не удалось: {ex.Message}", $"The device accepted the write, but verification failed: {ex.Message}"),
                UiText("Modbus: проверка записи", "Modbus: write verification"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RefreshSelected_Click(object sender, RoutedEventArgs e)
    {
        if(_scanCts!=null || _operationGate.CurrentCount==0){Log("Сначала остановите текущую операцию.");return;}
        if (RegistersGrid.SelectedItem is not RegisterRow row) return;
        try { await ReadBackAsync(row.Address, 1, row.Function); Log($"Повторное чтение адреса {row.Address} выполнено."); }
        catch (Exception ex) { row.Quality = "Ошибка"; Log("Повторное чтение: " + ex.Message); }
    }

    private sealed record RtuFound(string Com,int Baud,Parity Parity,int Data,StopBits Stop,byte Unit,string Details)
    {
        public override string ToString()=>$"{Com} {Baud}/{Parity}/{Data}/{Stop}, ID {Unit}: {Details}";
    }
    private void RtuFound_Selected(object sender, SelectionChangedEventArgs e)
    {
        if(_scanCts!=null || _operationGate.CurrentCount==0 || RtuFoundList.SelectedItem is not RtuFound hit)return;
        Disconnect();ProtocolBox.SelectedIndex=0;
        if(ComBox.Items.Contains(hit.Com))ComBox.SelectedItem=hit.Com;
        BaudBox.SelectedItem=BaudBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>x.Content.ToString()==hit.Baud.ToString());
        if(BaudBox.SelectedItem==null){BaudBox.Items.Add(new ComboBoxItem{Content=hit.Baud.ToString()});BaudBox.SelectedIndex=BaudBox.Items.Count-1;}
        ParityBox.SelectedIndex=hit.Parity==Parity.Even?1:hit.Parity==Parity.Odd?2:0;
        DataBitsBox.SelectedIndex=hit.Data-5;StopBitsBox.SelectedIndex=hit.Stop==StopBits.Two?1:0;UnitBox.Text=hit.Unit.ToString();
    }
    private int _rtuCursor;
    private string _rtuSignature = "";
    private async void ScanRtu_Click(object sender, RoutedEventArgs e)
    {
        if (IsTcp) { Log("Выберите RTU."); return; }
        if (_scanCts != null || _operationGate.CurrentCount==0) { Log("Сначала остановите текущую операцию."); return; }
        try
        {
            var com=ComBox.SelectedItem?.ToString() ?? throw new InvalidOperationException("Выберите COM-порт.");
            var rates=RtuBaudsBox.Text.Split(',').Select(x=>ParseInt(x.Trim(),300,1000000,"Скорость")).Distinct().ToArray();
            var parities=RtuParitiesBox.Text.Split(',').Select(x=>Enum.Parse<Parity>(x.Trim(),true)).Distinct().ToArray();
            if(rates.Length==0 || rates.Length>16 || parities.Length==0 || parities.Any(x=>x is not (Parity.None or Parity.Even or Parity.Odd)))throw new FormatException("Выберите None, Even, Odd.");
            int unitFrom=ParseInt(RtuUnitFromBox.Text,1,247,"ID от"),unitTo=ParseInt(RtuUnitToBox.Text,unitFrom,247,"ID до");
            byte fc=(byte)(FunctionBox.SelectedIndex+1); if(fc<3)throw new InvalidOperationException("Для автопоиска выберите FC03 или FC04.");
            ushort address=GetStart();
            int data=int.Parse(((ComboBoxItem)DataBitsBox.SelectedItem).Content.ToString()!);
            var stop=StopBitsBox.SelectedIndex==1?StopBits.Two:StopBits.One;
            var plan=(from baud in rates from parity in parities from unit in Enumerable.Range(unitFrom,unitTo-unitFrom+1) select (baud,parity,unit)).ToArray();
            string signature=$"{com}|{string.Join(',',rates)}|{string.Join(',',parities)}|{unitFrom}|{unitTo}|{fc}|{address}|{data}|{stop}";
            if(signature!=_rtuSignature || RtuResumeBox.IsChecked!=true || _rtuCursor>=plan.Length){_rtuCursor=0;RtuFoundList.Items.Clear();}
            _rtuSignature=signature;
            using var cts=new CancellationTokenSource();_scanCts=cts;SetBusy(true);Disconnect();_transportContext=ConnectionContext;
            try
            {
                for(;_rtuCursor<plan.Length;_rtuCursor++)
                {
                    cts.Token.ThrowIfCancellationRequested();var item=plan[_rtuCursor];
                    if(_serial==null || !_serial.IsOpen || _serial.BaudRate!=item.baud || _serial.Parity!=item.parity){
                        Disconnect();_serial=new SerialPort(com,item.baud,item.parity,data,stop){ReadTimeout=250,WriteTimeout=250};_serial.Open();
                    }
                    StatusText.Text=$"RTU {_rtuCursor+1}/{plan.Length}: {item.baud}/{item.parity}, ID {item.unit}";
                    try{await ReadAsync((byte)item.unit,fc,address,1,cts.Token);Log($"НАЙДЕНО {com} {item.baud}/{item.parity}/{data}/{stop}, ID {item.unit}");RtuFoundList.Items.Add(new RtuFound(com,item.baud,item.parity,data,stop,(byte)item.unit,"чтение подтверждено"));}
                    catch(ModbusDeviceException ex){Log($"НАЙДЕНО {com} {item.baud}/{item.parity}, ID {item.unit}: {ex.Message}");RtuFoundList.Items.Add(new RtuFound(com,item.baud,item.parity,data,stop,(byte)item.unit,ex.Message));}
                    catch(OperationCanceledException) when(cts.IsCancellationRequested){throw;}
                    catch(Exception ex){Log($"RTU ID {item.unit}: {ex.Message}");}
                }
                Log("Автопоиск RTU завершён.");
            }
            catch(OperationCanceledException){Log($"RTU остановлен на {_rtuCursor+1}/{plan.Length}; можно продолжить.");}
            finally{_scanCts=null;Disconnect();SetBusy(false);}
        }
        catch(Exception ex){Log("Автопоиск: "+ex.Message);}
    }

    private async void ScanRegisters_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCts != null || _operationGate.CurrentCount==0) { Log("Сначала остановите текущую операцию."); return; }
        _scanCts = new CancellationTokenSource();
        SetBusy(true);
        var ct = _scanCts.Token;
        try
        {
            await EnsureConnectedAsync(ct);
            int start = GetStart();
            int quantity = GetQuantity();
            if (start + quantity > 65536) throw new InvalidOperationException("Диапазон выходит за адрес 65535.");
            byte fc = (byte)(FunctionBox.SelectedIndex + 1);
            Log($"Поиск реализованных адресов: FC{fc}, {start}…{start + quantity - 1}.");
            int found = await ScanRegisterRangeAsync(GetUnit(), fc, start, quantity, ct);
            Log($"Поиск диапазона завершён. Ответивших адресов: {found}.");
        }
        catch (OperationCanceledException) { Log("Поиск регистров остановлен."); }
        catch (Exception ex) { Log("Ошибка поиска регистров: " + ex.Message); }
        finally { _scanCts?.Dispose(); _scanCts=null; SetBusy(false); }
    }

    private async Task<int> ScanRegisterRangeAsync(byte unit, byte function, int start, int quantity, CancellationToken ct)
    {
        int found = 0;
        int max = function is 1 or 2 ? 128 : 32;
        for (int offset = 0; offset < quantity; offset += max)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(max, quantity - offset);
            try
            {
                var values = await ReadAsync(unit, function, (ushort)(start + offset), (ushort)count, ct);
                ApplyReadValues((ushort)(start+offset), values, function);
                found+=values.Length;
                Log($"FC{function}: диапазон {start + offset}–{start + offset + count - 1} отвечает.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Retry isolated failures by splitting; preserve cancellation and avoid endless retries.
                if (count > 1)
                {
                    int left = count / 2;
                    found += await ScanRegisterRangeAsync(unit, function, start + offset, left, ct);
                    found += await ScanRegisterRangeAsync(unit, function, start + offset + left, count - left, ct);
                }
                else
                {
                    var row = _rows.FirstOrDefault(r => r.Address == start + offset && r.Function==function);
                    if (row is null) { row = new RegisterRow { Address = (ushort)(start + offset) }; _rows.Add(row); }
                    row.Function=function;
                    row.Quality = "No response / exception";
                    Log($"Адрес {start + offset}: {ex.Message}");
                }
            }
        }
        SortRows();
        UpdateInterpretedValues();
        return found;
    }

    private static readonly (int Port, string Service)[] LanPorts =
    {
        (80, "HTTP / веб-интерфейс"), (443, "HTTPS / веб-интерфейс"),
        (22, "SSH / сервер или сетевое устройство"), (23, "Telnet / промышленное или сетевое устройство"),
        (502, "Modbus TCP / ПЛК или шлюз"), (8080, "HTTP альтернативный"),
        (554, "RTSP / возможная IP-камера"), (3389, "RDP / Windows"),
        (445, "SMB / Windows или NAS"), (9100, "JetDirect / возможный принтер")
    };

    private CancellationTokenSource? _diagnosisCts;

    private void StopDiagnosis_Click(object sender, RoutedEventArgs e) => _diagnosisCts?.Cancel();

    private void AppendDiagnosis(string line)
    {
        DiagnosisOutputBox.AppendText(TranslateRuntime(line) + Environment.NewLine);
        DiagnosisOutputBox.ScrollToEnd();
    }

    private IPAddress GetDiagnosisAddress()
    {
        if (!IPAddress.TryParse(DiagHostBox.Text.Trim(), out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork)
            throw new FormatException(UiText("Введите корректный IPv4-адрес для диагностики.", "Enter a valid IPv4 address for diagnostics."));
        return ip;
    }

    private readonly Dictionary<string, string> _previousArpEntries = new(StringComparer.OrdinalIgnoreCase);

    private void ExportDiagnosis_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(DiagnosisOutputBox.Text))
            {
                Log(UiText("Отчёт диагностики пуст.", "Diagnostic report is empty."));
                return;
            }
            var dialog = new SaveFileDialog
            {
                Title = UiText("Сохранить отчёт диагностики", "Save diagnostic report"),
                Filter = UiText("Текстовый отчёт (*.txt)|*.txt", "Text report (*.txt)|*.txt"),
                FileName = "network-diagnostics.txt",
                DefaultExt = ".txt"
            };
            if (dialog.ShowDialog() != true) return;
            File.WriteAllText(dialog.FileName,
                $"{UiText("Modbus Scanner — диагностика сети", "Modbus Scanner — network diagnostics")}{Environment.NewLine}{UiText("Дата", "Date")}: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{Environment.NewLine}{DiagnosisOutputBox.Text}",
                new UTF8Encoding(true));
            Log("Отчёт диагностики сохранён: " + dialog.FileName);
        }
        catch (Exception ex) { Log("Экспорт диагностики: " + ex.Message); }
    }

    private async void DiagnoseArp_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosisCts != null) { Log("Диагностика уже выполняется."); return; }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            _diagnosisCts = cts;
            DiagnosisOutputBox.Clear();
            AppendDiagnosis("ARP: сравнение текущей таблицы Windows с предыдущим снимком");
            var start = new System.Diagnostics.ProcessStartInfo("arp.exe", "-a")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить arp.exe");
            string output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException("arp.exe вернул ошибку: " + process.ExitCode);
            var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in output.Split('\n'))
            {
                var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !IPAddress.TryParse(parts[0], out var ip) ||
                    ip.AddressFamily != AddressFamily.InterNetwork) continue;
                string mac = parts[1].ToLowerInvariant();
                if (!System.Text.RegularExpressions.Regex.IsMatch(mac, @"^([0-9a-f]{2}-){5}[0-9a-f]{2}$")) continue;
                current[ip.ToString()] = mac;
            }
            int changes = 0;
            foreach (var entry in current.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (_previousArpEntries.TryGetValue(entry.Key, out string? oldMac) &&
                    !string.Equals(oldMac, entry.Value, StringComparison.OrdinalIgnoreCase))
                {
                    changes++;
                    AppendDiagnosis($"ВНИМАНИЕ: {entry.Key}: MAC изменился {oldMac} → {entry.Value}");
                }
                else AppendDiagnosis($"{entry.Key,-16} {entry.Value}");
            }
            _previousArpEntries.Clear();
            foreach (var entry in current) _previousArpEntries[entry.Key] = entry.Value;
            AppendDiagnosis($"ARP-записей: {current.Count}; изменений MAC: {changes}");
            AppendDiagnosis("Смена MAC может быть штатной (DHCP, VRRP, смена устройства). Это только подозрение, не доказательство конфликта IP.");
            AppendDiagnosis("ARP-кэш неполон: отсутствие записи не означает отсутствие устройства.");
        }
        catch (OperationCanceledException) { AppendDiagnosis("ARP-проверка остановлена или истёк тайм-аут."); }
        catch (Exception ex) { AppendDiagnosis("Ошибка ARP: " + ex.Message); }
        finally { _diagnosisCts = null; }
    }

    private async void DiagnoseCompare_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosisCts != null) { Log("Диагностика уже выполняется."); return; }
        try
        {
            IPAddress target = GetDiagnosisAddress();
            var adapter = LanAdapterBox.SelectedItem as LanAdapterChoice;
            if (adapter is null) { RefreshLanAdapters(); adapter = LanAdapterBox.SelectedItem as LanAdapterChoice; }
            if (adapter is null) throw new InvalidOperationException("Выберите сетевой адаптер.");
            IPAddress? gateway = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.Name == adapter.Adapter)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (gateway is null) throw new InvalidOperationException("IPv4-шлюз не найден.");
            using var cts = new CancellationTokenSource();
            _diagnosisCts = cts;
            DiagnosisOutputBox.Clear();
            AppendDiagnosis($"Сравнение: шлюз {gateway} / цель {target}, по 5 ICMP-запросов");
            async Task<(int Received, double Average)> Measure(IPAddress ip)
            {
                int received = 0;
                long total = 0;
                using var ping = new Ping();
                for (int i = 0; i < 5; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var reply = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(1000),
                            Array.Empty<byte>(), new PingOptions(), cts.Token);
                        if (reply.Status == IPStatus.Success)
                        {
                            received++;
                            total += reply.RoundtripTime;
                        }
                    }
                    catch (PingException ex) { AppendDiagnosis($"ICMP {ip}: {ex.Message}"); }
                }
                return (received, received == 0 ? 0 : (double)total / received);
            }
            var gw = await Measure(gateway);
            var dst = await Measure(target);
            AppendDiagnosis($"Шлюз: ответов {gw.Received}/5, средняя RTT {gw.Average:F1} мс");
            AppendDiagnosis($"Цель:  ответов {dst.Received}/5, средняя RTT {dst.Average:F1} мс");
            if (gw.Received == 0) AppendDiagnosis("Шлюз не отвечает на ICMP. Возможна фильтрация; проверьте локальное соединение.");
            else if (dst.Received == 0) AppendDiagnosis("Цель не отвечает на ICMP. Проверьте маршрут, фильтрацию и доступность сервиса.");
            else if (dst.Average > gw.Average + 50)
                AppendDiagnosis("У цели RTT более чем на 50 мс выше шлюза. Проверьте маршрут и нагрузку; причина не установлена.");
            else AppendDiagnosis("По этим измерениям значительного превышения RTT цели над шлюзом не обнаружено.");
            AppendDiagnosis("Измерения последовательные, не одновременные. Для устойчивого вывода повторите проверку.");
        }
        catch (OperationCanceledException) { AppendDiagnosis("Сравнение остановлено."); }
        catch (Exception ex) { AppendDiagnosis("Ошибка сравнения: " + ex.Message); }
        finally { _diagnosisCts = null; }
    }

    private async void DiagnoseDns_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosisCts != null) { Log("Диагностика уже выполняется."); return; }
        try
        {
            string target = DiagHostBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(target)) throw new FormatException("Введите IP или DNS-имя.");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _diagnosisCts = cts;
            DiagnosisOutputBox.Clear();
            AppendDiagnosis($"DNS: {target}");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var addresses = await Dns.GetHostAddressesAsync(target, cts.Token);
            watch.Stop();
            AppendDiagnosis($"Разрешение имени: {watch.ElapsedMilliseconds} мс");
            foreach (var address in addresses) AppendDiagnosis($"  {address}");
            if (addresses.Length == 0) AppendDiagnosis("DNS не вернул адресов.");
            if (IPAddress.TryParse(target, out _))
                AppendDiagnosis("Введён IP-адрес: это не проверка прямого разрешения DNS-имени.");
        }
        catch (OperationCanceledException) { AppendDiagnosis("DNS: время ожидания истекло или проверка отменена."); }
        catch (Exception ex) { AppendDiagnosis("Ошибка DNS: " + ex.Message); }
        finally { _diagnosisCts = null; }
    }

    private async void DiagnoseGateway_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosisCts != null) { Log("Диагностика уже выполняется."); return; }
        try
        {
            var adapter = LanAdapterBox.SelectedItem as LanAdapterChoice;
            if (adapter is null) { RefreshLanAdapters(); adapter = LanAdapterBox.SelectedItem as LanAdapterChoice; }
            if (adapter is null) throw new InvalidOperationException("Сетевой адаптер не выбран.");
            var gateway = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.Name == adapter.Adapter)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            if (gateway is null) throw new InvalidOperationException("Для выбранного адаптера IPv4-шлюз не найден.");
            DiagHostBox.Text = gateway.ToString();
            DiagnosePing_Click(sender, e);
        }
        catch (Exception ex) { AppendDiagnosis("Проверка шлюза: " + ex.Message); }
        await Task.CompletedTask;
    }

    private async void DiagnoseTcp_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosisCts != null) { Log("Диагностика уже выполняется."); return; }
        try
        {
            IPAddress address = GetDiagnosisAddress();
            int port = ParseInt(DiagPortBox.Text, 1, 65535, "TCP-порт");
            using var cts = new CancellationTokenSource();
            _diagnosisCts = cts;
            DiagnosisOutputBox.Clear();
            AppendDiagnosis($"Проверка TCP {address}:{port} — 4 подключения, тайм-аут 1500 мс");
            int successes = 0;
            var times = new List<long>();
            for (int i = 1; i <= 4; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                using var client = new TcpClient();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(1500));
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await client.ConnectAsync(address, port, deadline.Token);
                    timer.Stop();
                    successes++;
                    times.Add(timer.ElapsedMilliseconds);
                    AppendDiagnosis($"{i}: соединение установлено за {timer.ElapsedMilliseconds} мс");
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { AppendDiagnosis($"{i}: тайм-аут соединения"); }
                catch (SocketException ex) { AppendDiagnosis($"{i}: TCP {ex.SocketErrorCode}"); }
            }
            AppendDiagnosis($"Успешно: {successes}/4");
            if (times.Count > 0)
                AppendDiagnosis($"Время TCP подключения мин/сред/макс: {times.Min()}/{times.Average():F1}/{times.Max()} мс");
            AppendDiagnosis("Успешный TCP connect не подтверждает исправность Modbus или другого прикладного протокола.");
        }
        catch (OperationCanceledException) { AppendDiagnosis("Диагностика остановлена."); }
        catch (Exception ex) { AppendDiagnosis("Ошибка TCP-диагностики: " + ex.Message); }
        finally { _diagnosisCts = null; }
    }

    private async void DiagnoseMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosisCts != null) { Log("Диагностика уже выполняется."); return; }
        try
        {
            var address = GetDiagnosisAddress();
            using var cts = new CancellationTokenSource();
            _diagnosisCts = cts;
            DiagnosisOutputBox.Clear();
            DiagnosisChartPanel.Children.Clear();
            AppendDiagnosis($"Мониторинг {address}: 20 измерений с интервалом 1 с");
            int received = 0;
            var times = new List<long>();
            using var ping = new Ping();
            for (int i = 1; i <= 20; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                long? ms = null;
                try
                {
                    var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(1000),
                        Array.Empty<byte>(), new PingOptions(), cts.Token);
                    if (reply.Status == IPStatus.Success)
                    {
                        ms = reply.RoundtripTime;
                        received++;
                        times.Add(ms.Value);
                        AppendDiagnosis($"{i,2}: {ms} мс");
                    }
                    else AppendDiagnosis($"{i,2}: {reply.Status}");
                }
                catch (PingException ex) { AppendDiagnosis($"{i,2}: ICMP: {ex.Message}"); }

                // Bars represent RTT; red marks missing replies. Scale is capped at 500 ms.
                var bar = new Border
                {
                    Width = 13,
                    Height = ms.HasValue ? Math.Max(4, Math.Min(110, ms.Value * 110.0 / 500.0)) : 110,
                    Background = ms.HasValue ? (ms.Value > 100 ? Brushes.Orange : Brushes.MediumSeaGreen) : Brushes.IndianRed,
                    Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    ToolTip = ms.HasValue ? $"Измерение {i}: {ms} мс" : $"Измерение {i}: нет ответа"
                };
                DiagnosisChartPanel.Children.Add(bar);
                if (i < 20) await Task.Delay(1000, cts.Token);
            }
            AppendDiagnosis($"Ответов: {received}/20; потери: {(20 - received) * 5}%");
            if (times.Count > 0)
                AppendDiagnosis($"RTT мин/сред/макс: {times.Min()}/{times.Average():F1}/{times.Max()} мс");
            AppendDiagnosis("График: зелёный — ответ до 100 мс, оранжевый — более 100 мс, красный — нет ответа. Высота ограничена 500 мс.");
        }
        catch (OperationCanceledException) { AppendDiagnosis("Мониторинг остановлен."); }
        catch (Exception ex) { AppendDiagnosis("Ошибка мониторинга: " + ex.Message); }
        finally { _diagnosisCts = null; }
    }

    private async void DiagnosePing_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosisCts != null) { Log("Диагностика уже выполняется."); return; }
        try
        {
            IPAddress address = GetDiagnosisAddress();
            using var cts = new CancellationTokenSource();
            _diagnosisCts = cts;
            DiagnosisOutputBox.Clear();
            AppendDiagnosis($"Ping {address}: 4 запроса, тайм-аут 1000 мс");
            int received = 0;
            var durations = new List<long>();
            using var ping = new Ping();
            for (int i = 1; i <= 4; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                try
                {
                    var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(1000), Array.Empty<byte>(), new PingOptions(), cts.Token);
                    if (reply.Status == IPStatus.Success)
                    {
                        received++;
                        durations.Add(reply.RoundtripTime);
                        AppendDiagnosis($"{i}: ответ {reply.Address}, {reply.RoundtripTime} мс");
                    }
                    else AppendDiagnosis($"{i}: {reply.Status}");
                }
                catch (PingException ex) { AppendDiagnosis($"{i}: ошибка ICMP: {ex.Message}"); }
            }
            AppendDiagnosis($"Получено: {received}/4, потери: {(4 - received) * 25}%");
            if (durations.Count > 0)
                AppendDiagnosis($"RTT мин/сред/макс: {durations.Min()}/{durations.Average():F1}/{durations.Max()} мс");
            AppendDiagnosis("ICMP может блокироваться устройством или межсетевым экраном.");
        }
        catch (OperationCanceledException) { AppendDiagnosis("Диагностика остановлена."); }
        catch (Exception ex) { AppendDiagnosis("Ошибка Ping: " + ex.Message); }
        finally { _diagnosisCts = null; }
    }

    private async void DiagnoseTrace_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosisCts != null) { Log("Диагностика уже выполняется."); return; }
        try
        {
            IPAddress address = GetDiagnosisAddress();
            using var cts = new CancellationTokenSource();
            _diagnosisCts = cts;
            DiagnosisOutputBox.Clear();
            AppendDiagnosis($"Трассировка до {address}, максимум 20 переходов (ICMP)");
            using var ping = new Ping();
            for (int ttl = 1; ttl <= 20; ttl++)
            {
                cts.Token.ThrowIfCancellationRequested();
                try
                {
                    var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(1200),
                        new byte[32], new PingOptions(ttl, true), cts.Token);
                    string hop = reply.Address?.ToString() ?? "*";
                    AppendDiagnosis($"{ttl,2}: {hop,-16} {reply.Status} {(reply.Status == IPStatus.Success || reply.Status == IPStatus.TtlExpired ? reply.RoundtripTime + " мс" : "")}");
                    if (reply.Status == IPStatus.Success) { AppendDiagnosis("Целевой адрес достигнут."); break; }
                    if (reply.Status == IPStatus.DestinationHostUnreachable ||
                        reply.Status == IPStatus.DestinationNetworkUnreachable) break;
                }
                catch (PingException ex) { AppendDiagnosis($"{ttl,2}: ICMP: {ex.Message}"); }
            }
            AppendDiagnosis("Звёздочки и тайм-ауты не обязательно означают неисправность узла.");
        }
        catch (OperationCanceledException) { AppendDiagnosis("Трассировка остановлена."); }
        catch (Exception ex) { AppendDiagnosis("Ошибка трассировки: " + ex.Message); }
        finally { _diagnosisCts = null; }
    }

    private void ClearLanResults_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCts != null) { Log("Сначала остановите поиск, затем очистите результаты."); return; }
        LanResultsList.Items.Clear();
        DrawLanMap();
        Log("Результаты IP-сканера и карта очищены.");
    }

    private void ExportLanCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entries = LanResultsList.Items.OfType<string>().ToArray();
            if (entries.Length == 0) { Log("Экспорт IP-сканера: список пуст."); return; }
            var dialog = new SaveFileDialog
            {
                Title = "Экспорт результатов IP-сканера",
                Filter = "CSV (*.csv)|*.csv",
                FileName = "lan-scan.csv",
                DefaultExt = ".csv"
            };
            if (dialog.ShowDialog() != true) return;
            static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
            var lines = new List<string> { "IP,DNS,TCP" };
            foreach (string entry in entries)
            {
                var fields = entry.Split('|', 3);
                if (fields.Length != 3) continue;
                lines.Add(string.Join(",", Csv(fields[0].Trim()),
                    Csv(fields[1].Trim().Replace("DNS: ", "")),
                    Csv(fields[2].Trim().Replace("TCP: ", ""))));
            }
            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
            Log($"Экспортировано {lines.Count - 1} устройств: {dialog.FileName}");
        }
        catch (Exception ex) { Log("Ошибка экспорта IP-сканера: " + ex.Message); }
    }

    private sealed record LanAdapterChoice(string Adapter, IPAddress Address, IPAddress Mask, bool Gateway)
    {
        public override string ToString() => $"{Adapter} — {Address}" + (Gateway ? " (шлюз)" : "");
    }

    private List<LanAdapterChoice> GetLanAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is not null)
                .Select(a => new LanAdapterChoice(n.Name, a.Address, a.IPv4Mask!,
                    n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))))
            .Where(a => !IPAddress.IsLoopback(a.Address) &&
                        !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .OrderByDescending(a => a.Gateway)
            .ThenBy(a => a.Adapter, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void RefreshLanAdapters_Click(object sender, RoutedEventArgs e) => RefreshLanAdapters();

    private void RefreshLanAdapters()
    {
        try
        {
            var previous = (LanAdapterBox.SelectedItem as LanAdapterChoice)?.Address;
            var adapters = GetLanAdapters();
            LanAdapterBox.ItemsSource = adapters;
            LanAdapterBox.SelectedItem = adapters.FirstOrDefault(a => a.Address.Equals(previous)) ?? adapters.FirstOrDefault();
            Log($"Доступно IPv4-адаптеров: {adapters.Count}.");
        }
        catch (Exception ex) { Log("Список адаптеров: " + ex.Message); }
    }

    private void DetectLanSubnet_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LanAdapterBox.Items.Count == 0) RefreshLanAdapters();
            if (LanAdapterBox.SelectedItem is not LanAdapterChoice selected)
                throw new InvalidOperationException("Активный IPv4-адаптер не найден. Укажите диапазон вручную.");
            uint mask = IpToUInt(selected.Mask);
            uint network = IpToUInt(selected.Address) & mask;
            uint broadcast = network | ~mask;
            uint first = network < broadcast ? network + 1 : network;
            uint last = broadcast > network ? broadcast - 1 : broadcast;
            if (last < first || last - first > 1023)
            {
                network = IpToUInt(selected.Address) & 0xFFFFFF00u;
                first = network + 1;
                last = network + 254;
                Log("Подсеть больше 1024 адресов: выбран локальный диапазон /24.");
            }
            LanStartBox.Text = UIntToIp(first).ToString();
            LanEndBox.Text = UIntToIp(last).ToString();
            Log($"Выбран адаптер {selected.Adapter}: {selected.Address}, маска {selected.Mask}; диапазон {LanStartBox.Text}–{LanEndBox.Text}.");
        }
        catch (Exception ex) { Log("Определение подсети: " + ex.Message); }
    }

    private void RefreshLanMap_Click(object sender, RoutedEventArgs e) => DrawLanMap();

    private void DrawLanMap()
    {
        LanMapCanvas.Children.Clear();
        var allHosts = LanResultsList.Items.OfType<string>()
            .OrderBy(item => IPAddress.TryParse(item.Split('|')[0].Trim(), out var ip) ? IpToUInt(ip) : uint.MaxValue)
            .ToList();
        const int mapLimit = 150;
        var hosts = allHosts.Take(mapLimit).ToList();
        int modbusCount = allHosts.Count(item => item.Contains("502 (", StringComparison.Ordinal));
        int cameraCount = allHosts.Count(item => item.Contains("554 (", StringComparison.Ordinal));
        const double width = 900, cardWidth = 250, cardHeight = 74;
        LanMapCanvas.Width = width;
        double y = 12;
        AddMapCard($"Устройств: {allHosts.Count}", $"Возможный Modbus: {modbusCount} · RTSP: {cameraCount}", 325, y, 250, 68, Brushes.DarkSlateBlue);
        y += 90;
        if (allHosts.Count > mapLimit)
        {
            AddMapCard($"Показано {mapLimit} из {allHosts.Count}", "Остальные устройства доступны в списке и CSV", 325, y, 250, 68, Brushes.DarkSlateBlue);
            y += 90;
        }
        if (hosts.Count == 0)
        {
            AddMapCard("Нет результатов", "Сначала выполните IP-поиск", 325, 80, 250, 74, Brushes.DarkSlateGray);
            LanMapCanvas.Height = 300;
            return;
        }

        // /24 is a visual grouping convention, not a claim about the actual subnet mask.
        var groups = hosts.GroupBy(item =>
        {
            string ip = item.Split('|')[0].Trim();
            if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                return "Другие адреса";
            var bytes = address.GetAddressBytes();
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        });
        foreach (var group in groups)
        {
            var entries = group.ToList();
            double rootY = y;
            AddMapCard(group.Key, $"Условная группа /24 · устройств: {entries.Count}", 325, rootY, 250, 68, Brushes.DarkSlateBlue);
            y += 100;
            for (int i = 0; i < entries.Count; i++)
            {
                int col = i % 3, row = i / 3;
                double x = 24 + col * 290, nodeY = y + row * 110;
                string[] fields = entries[i].Split('|');
                string ip = fields[0].Trim();
                string details = fields.Length > 2 ? fields[2].Trim() : "Службы неизвестны";
                string kind = details.Contains("502 (") ? "Возможный Modbus TCP" :
                    details.Contains("554 (") ? "Возможная IP-камера" :
                    details.Contains("9100 (") ? "Возможный принтер" :
                    details.Contains("445 (") ? "Возможный ПК/NAS" :
                    details.Contains("22 (") || details.Contains("23 (") ? "Сетевое устройство/сервер" :
                    "Устройство с TCP-службой";
                var line = new Line
                {
                    X1 = 450, Y1 = rootY + 68, X2 = x + cardWidth / 2, Y2 = nodeY,
                    Stroke = Brushes.SlateGray, StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 5 },
                    IsHitTestVisible = false
                };
                LanMapCanvas.Children.Add(line);
                string dns = fields.Length > 1 ? fields[1].Trim() : "";
                AddMapCard(ip, kind, x, nodeY, cardWidth, cardHeight, Brushes.DarkSlateGray, ip, dns + "\n" + details);
            }
            y += ((entries.Count + 2) / 3) * 110 + 32;
        }
        LanMapCanvas.Height = Math.Max(300, y);
    }

    private void AddMapCard(string heading, string detail, double x, double y, double width, double height, Brush background, string? deviceIp = null, string? extraDetails = null)
    {
        var panel = new StackPanel { Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = detail, Foreground = Brushes.LightCyan, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var border = new Border
        {
            Width = width, Height = height, Background = background,
            BorderBrush = Brushes.CadetBlue, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Child = panel
        };
        if (deviceIp is not null)
        {
            border.Cursor = System.Windows.Input.Cursors.Hand;
            border.ToolTip = $"Нажмите, чтобы выбрать устройство для Modbus TCP.\\n{extraDetails}".Replace("\\n", "\n");
            border.MouseLeftButtonUp += (_, _) => SelectLanHost(deviceIp);
        }
        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, y);
        LanMapCanvas.Children.Add(border);
    }

    private void SelectLanHost(string ip)
    {
        if (_scanCts != null) return;
        ProtocolBox.SelectedIndex = 1;
        HostBox.Text = ip;
        PortBox.Text = "502";
        Log($"Выбран узел {ip} с карты сети. Для Modbus TCP проверьте порт и Unit ID.");
    }

    private void LanResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_scanCts != null || LanResultsList.SelectedItem is not string result) return;
        string ip = result.Split('|')[0].Trim();
        if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork) return;
        ProtocolBox.SelectedIndex = 1;
        HostBox.Text = ip;
        PortBox.Text = "502";
        Log($"IP {ip} перенесён в подключение Modbus TCP. Порт 502 установлен по умолчанию; наличие Modbus на этом адресе ещё не подтверждено.");
    }

    private async void ScanLan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCts != null || _operationGate.CurrentCount == 0)
        {
            Log("Остановите текущую операцию перед IP-поиском.");
            return;
        }
        try
        {
            if (!IPAddress.TryParse(LanStartBox.Text.Trim(), out var first) ||
                !IPAddress.TryParse(LanEndBox.Text.Trim(), out var last) ||
                first.AddressFamily != AddressFamily.InterNetwork || last.AddressFamily != AddressFamily.InterNetwork)
                throw new FormatException("Введите корректные IPv4-адреса.");
            uint start = IpToUInt(first), end = IpToUInt(last);
            if (end < start || end - start > 1023)
                throw new FormatException("Допустимый диапазон: от 1 до 1024 адресов.");
            int timeout = ParseInt(LanTimeoutBox.Text, 100, 3000, "Тайм-аут");
            LanResultsList.Items.Clear();
            using var cts = new CancellationTokenSource();
            _scanCts = cts;
            SetBusy(true);
            try
            {
                using var gate = new SemaphoreSlim(16);
                int completed = 0, found = 0;
                var tasks = Enumerable.Range(0, (int)(end - start + 1)).Select(async offset =>
                {
                    await gate.WaitAsync(cts.Token);
                    try
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        string ip = UIntToIp(start + (uint)offset).ToString();
                        var open = new List<string>();
                        foreach (var (port, service) in LanPorts)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            try
                            {
                                using var client = new TcpClient();
                                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                                deadline.CancelAfter(timeout);
                                await client.ConnectAsync(ip, port, deadline.Token);
                                open.Add($"{port} ({service})");
                            }
                            catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                            catch (Exception) { /* Filtered port or timeout; not proof host is absent. */ }
                        }
                        if (open.Count > 0)
                        {
                            string hostName = "не определено";
                            try
                            {
                                var dns = await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromMilliseconds(timeout), cts.Token);
                                hostName = dns.HostName;
                            }
                            catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                            catch (Exception) { }
                            Interlocked.Increment(ref found);
                            await Dispatcher.InvokeAsync(() =>
                                LanResultsList.Items.Add($"{ip} | DNS: {hostName} | TCP: {string.Join(", ", open)}"));
                        }
                        int done = Interlocked.Increment(ref completed);
                        if (done % 10 == 0 || done == (int)(end - start + 1))
                            await Dispatcher.InvokeAsync(() => StatusText.Text = UiText($"IP-поиск {done}/{end - start + 1}, узлов с TCP: {Volatile.Read(ref found)}", $"IP scan {done}/{end - start + 1}, hosts with TCP: {Volatile.Read(ref found)}"));
                    }
                    finally { gate.Release(); }
                }).ToArray();
                await Task.WhenAll(tasks);
                DrawLanMap();
                Log($"IP-поиск завершён: найдено {found} адресов с отвечающими TCP-службами.");
            }
            catch (OperationCanceledException) { DrawLanMap(); Log("IP-поиск остановлен; уже найденные узлы сохранены на карте."); }
            finally { _scanCts = null; SetBusy(false); }
        }
        catch (Exception ex) { Log("IP-поиск: " + ex.Message); }
    }

    private async void ScanTcpNetwork_Click(object sender, RoutedEventArgs e)
    {
        if(_scanCts!=null || _operationGate.CurrentCount==0){Log("Сначала остановите текущую операцию.");return;}
        try
        {
            if(!IPAddress.TryParse(TcpScanStartBox.Text,out var startIp) || startIp.AddressFamily!=AddressFamily.InterNetwork || !IPAddress.TryParse(TcpScanEndBox.Text,out var endIp) || endIp.AddressFamily!=AddressFamily.InterNetwork)throw new FormatException("Нужны адреса IPv4.");
            uint start=IpToUInt(startIp),end=IpToUInt(endIp);
            if(end<start || end-start>4095)throw new FormatException("Диапазон: 1–4096 адресов.");
            var ports=TcpScanPortBox.Text.Split(',').Select(x=>ParseInt(x.Trim(),1,65535,"Порт")).Distinct().ToArray();
            if(ports.Length>16)throw new FormatException("Не более 16 портов.");
            int timeout=ParseInt(TcpScanTimeoutBox.Text,100,5000,"Тайм-аут");
            byte unit=GetUnit(),fc=(byte)(FunctionBox.SelectedIndex+1);ushort address=GetStart();
            if(fc<3)throw new InvalidOperationException("Для поиска выберите FC03 или FC04.");
            TcpScanResultsList.Items.Clear();
            using var cts=new CancellationTokenSource();_scanCts=cts;SetBusy(true);
            int checkedCount=0,found=0;long total=((long)end-start+1)*ports.Length;
            try
            {
                // Bounded batches keep cancellation/cleanup deterministic; each probe owns its socket.
                for(ulong current=start;current<=end;current++)
                {
                    foreach(var port in ports)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        string host=UIntToIp((uint)current).ToString();
                        try{
                            string? result=await TcpProbe.RunAsync(host,port,unit,fc,address,timeout,cts.Token);
                            if(result!=null){TcpScanResultsList.Items.Add($"{host}:{port} | {result}");found++;}
                        }
                        catch(OperationCanceledException) when(cts.IsCancellationRequested){throw;}
                        catch{ }
                        checkedCount++;StatusText.Text=UiText($"TCP {checkedCount}/{total}, Modbus: {found}", $"TCP {checkedCount}/{total}, Modbus: {found}");
                    }
                }
                Log($"TCP завершён: {found} подтверждённых устройств, {checkedCount} проверок.");
            }
            catch(OperationCanceledException){Log($"TCP остановлен: {checkedCount}/{total}.");}
            finally{_scanCts=null;SetBusy(false);}
        }
        catch(Exception ex){Log("TCP-поиск: "+ex.Message);}
    }

    private void TcpScanResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_scanCts!=null || _operationGate.CurrentCount==0 || TcpScanResultsList.SelectedItem is not string selectedEndpoint) return;
        string endpoint=selectedEndpoint.Split('|')[0].Trim();
        int separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(endpoint[(separator + 1)..], out int port)) return;
        HostBox.Text = endpoint[..separator];
        PortBox.Text = port.ToString(CultureInfo.InvariantCulture);
        ProtocolBox.SelectedIndex = 1;
        Log($"Выбран TCP-узел {endpoint}. Укажите Unit ID и нажмите «Подключить / проверить» для подтверждения Modbus.");
    }

    private static uint IpToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress UIntToIp(uint ip) => new(new byte[]
    {
        (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip
    });

    private void StopScan_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV UTF-8 (*.csv)|*.csv", FileName = "modbus-registers.csv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var sb = new StringBuilder("Address;Decimal;Hex;Binary;Interpreted;Description;Quality\r\n");
            foreach (var row in _rows.OrderBy(r => r.Address))
            {
                var fields = new[]
                {
                    row.Address.ToString(CultureInfo.InvariantCulture),
                    row.RawValue.ToString(CultureInfo.InvariantCulture),
                    row.HexValue,
                    row.BinaryValue,
                    row.DisplayValue,
                    row.Description,
                    row.Quality
                };
                sb.AppendLine(string.Join(";", fields.Select(CsvEscape)));
            }
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
            Log($"Экспортировано {_rows.Count} строк в {dialog.FileName}");
        }
        catch (Exception ex) { Log("Ошибка экспорта CSV: " + ex.Message); }
    }

    private static string CsvEscape(string? value)
    {
        value ??= "";
        return "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }

    private void SortRows()
    {
        var ordered = _rows.OrderBy(r => r.Address).ToList();
        _rows.Clear();
        foreach (var row in ordered) _rows.Add(row);
    }

    private byte GetUnit() => (byte)ParseInt(UnitBox.Text, 1, 247, "Unit ID");
    private ushort GetStart() => ParseUShort(StartAddressBox.Text, "Стартовый адрес");
    private ushort GetQuantity() => (ushort)ParseInt(QuantityBox.Text, 1, 2000, "Количество");
    private static ushort ParseUShort(string text, string field)
    {
        if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return v;
        throw new FormatException($"{field}: введите число 0–65535 (можно 0xHEX).");
    }
    private static int ParseInt(string text, int min, int max, string field)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max) return v;
        throw new FormatException($"{field}: допустимый диапазон {min}–{max}.");
    }

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(message)); return; }
        LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss.fff}] {TranslateRuntime(message)}");
        while (LogList.Items.Count > 1000) LogList.Items.RemoveAt(LogList.Items.Count - 1);
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveLocalSettings();
        _scanCts?.Cancel();
        Disconnect();
        if (_simulator is not null)
        {
            // Stop the loopback listener and connected clients when the window closes.
            _ = _simulator.DisposeAsync();
            _simulator = null;
        }
        base.OnClosed(e);
    }
}
