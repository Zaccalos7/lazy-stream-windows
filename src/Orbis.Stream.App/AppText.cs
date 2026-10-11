using System.Globalization;

namespace Orbis.Stream.App;

/// <summary>
/// Simple hardcoded text provider for the desktop shell.
/// The startup happens before Kestrel and the ASP.NET Core I18n services are ready.
/// </summary>
public static class AppText
{
    private static string Language => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    public static string Loading => Language switch
    {
        "it" => "Caricamento di Orbis Stream...",
        "de" => "Orbis Stream wird geladen...",
        "es" => "Cargando Orbis Stream...",
        "fr" => "Chargement d'Orbis Stream...",
        "pt" => "Carregando Orbis Stream...",
        "ru" => "Загрузка Orbis Stream...",
        "zh" => "正在加载 Orbis Stream...",
        "ko" => "Orbis Stream 로딩 중...",
        "ja" => "Orbis Streamを読み込んでいます...",
        "tr" => "Orbis Stream yükleniyor...",
        "la" => "Orbis Stream parātur...",
        "tlh" => "Orbis Stream yInI'Daq...",
        "hod" => "Hodor Orbis Stream...",
        _ => "Loading Orbis Stream..." // English fallback
    };

    public static string BrowserInitError(string message) => Language switch
    {
        "it" => $"Impossibile inizializzare il browser: {message}",
        "de" => $"Browser konnte nicht initialisiert werden: {message}",
        "es" => $"No se pudo inicializar el navegador: {message}",
        "fr" => $"Impossible d'initialiser le navigateur : {message}",
        "pt" => $"Não foi possível inicializar o navegador: {message}",
        "ru" => $"Не удалось инициализировать браузер: {message}",
        "zh" => $"无法初始化浏览器: {message}",
        "ko" => $"브라우저를 초기화할 수 없습니다: {message}",
        "ja" => $"ブラウザを初期化できません: {message}",
        "tr" => $"Tarayıcı başlatılamadı: {message}",
        "la" => $"Nāvigātrum initium nōn potuit: {message}",
        "tlh" => $"Daj Qapla' be': {message}",
        "hod" => $"Hodor: {message}",
        _ => $"Unable to initialize the browser: {message}"
    };

    public static string BrowserReachError(string url) => Language switch
    {
        "it" => $"Il browser non riesce a raggiungere {url}.",
        "de" => $"Der Browser kann {url} nicht erreichen.",
        "es" => $"El navegador no puede alcanzar {url}.",
        "fr" => $"Le navigateur ne parvient pas à joindre {url}.",
        "pt" => $"O navegador não consegue alcançar {url}.",
        "ru" => $"Браузер не может получить доступ к {url}.",
        "zh" => $"浏览器无法访问 {url}。",
        "ko" => $"브라우저가 {url}에 도달할 수 없습니다.",
        "ja" => $"ブラウザは {url} に到達できません。",
        "tr" => $"Tarayıcı {url} adresine erişemiyor.",
        "la" => $"Nāvigātrum {url} nōn potuit reperīre.",
        "tlh" => $"Daj {url} Qapla' be'.",
        "hod" => $"Hodor {url}.",
        _ => $"The browser cannot reach {url}."
    };

    public static string StartError(string message) => Language switch
    {
        "it" => $"Impossibile avviare Orbis Stream:\n{message}",
        "de" => $"Orbis Stream konnte nicht gestartet werden:\n{message}",
        "es" => $"No se pudo iniciar Orbis Stream:\n{message}",
        "fr" => $"Impossible de démarrer Orbis Stream :\n{message}",
        "pt" => $"Não foi possível iniciar o Orbis Stream:\n{message}",
        "ru" => $"Не удалось запустить Orbis Stream:\n{message}",
        "zh" => $"无法启动 Orbis Stream:\n{message}",
        "ko" => $"Orbis Stream을(를) 시작할 수 없습니다:\n{message}",
        "ja" => $"Orbis Streamを開始できません:\n{message}",
        "tr" => $"Orbis Stream başlatılamadı:\n{message}",
        "la" => $"Orbis Stream nōn potuit incipere:\n{message}",
        "tlh" => $"Orbis Stream taghlaHbe':\n{message}",
        "hod" => $"Hodor Orbis Stream:\n{message}",
        _ => $"Unable to start Orbis Stream:\n{message}"
    };

    public static string UnexpectedError(string message) => Language switch
    {
        "it" => $"Errore inatteso:\n{message}",
        "de" => $"Unerwarteter Fehler:\n{message}",
        "es" => $"Error inesperado:\n{message}",
        "fr" => $"Erreur inattendue :\n{message}",
        "pt" => $"Erro inesperado:\n{message}",
        "ru" => $"Неожиданная ошибка:\n{message}",
        "zh" => $"意外错误:\n{message}",
        "ko" => $"예기치 않은 오류:\n{message}",
        "ja" => $"予期しないエラー:\n{message}",
        "tr" => $"Beklenmeyen hata:\n{message}",
        "la" => $"Error inexpectātus:\n{message}",
        "tlh" => $"Qagh inexpectatus:\n{message}",
        "hod" => $"Hodor:\n{message}",
        _ => $"Unexpected error:\n{message}"
    };
}
