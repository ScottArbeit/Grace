namespace Grace.Shared.Resources

open NodaTime
open System

module Text =

    /// Computes text for how long ago an instant was.
    ///
    /// You can pass the two instants in either order. The language code should be a two-letter language code, such as "en" or "es".
    let ago language (instant: Instant) =
        let instant2 = SystemClock.Instance.GetCurrentInstant()
        let since = if instant2 > instant then instant2.Minus(instant) else instant.Minus(instant2)
        
        let totalSeconds = since.TotalSeconds
        let totalMinutes = since.TotalMinutes
        let totalHours = since.TotalHours
        let totalDays = since.TotalDays
        
        match language with
        | "en" -> (* English *)
            if totalSeconds < 2.0 then $"1 second ago"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} seconds ago"
            elif totalMinutes < 2.0 then $"1 minute ago"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} minutes ago"
            elif totalHours < 2.0 then $"1 hour ago"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} hours ago"
            elif totalDays < 2.0 then $"1 day ago"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} days ago"
            elif totalDays < 60.0 then $"1 month ago"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} months ago"
            elif totalDays < 730.5 then $"1 year ago"
            else $"{Math.Floor(totalDays / 365.25):F0} years ago"
        | "zh" -> (* Chinese *)
            if totalSeconds < 2.0 then $"1 秒前"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} 秒前"
            elif totalMinutes < 2.0 then $"1 分钟前"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} 分钟前"
            elif totalHours < 2.0 then $"1 小时前"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} 小时前"
            elif totalDays < 2.0 then $"1 天前"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} 天前"
            elif totalDays < 60.0 then $"1 个月前"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} 个月前"
            elif totalDays < 730.5 then $"1 年前"
            else $"{Math.Floor(totalDays / 365.25):F0} 年前"
        | "jp" -> (* Japanese *)
            if totalSeconds < 2.0 then $"1 秒前"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} 秒前"
            elif totalMinutes < 2.0 then $"1 分前"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} 分前"
            elif totalHours < 2.0 then $"1 時間前"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} 時間前"
            elif totalDays < 2.0 then $"1 日前"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} 日前"
            elif totalDays < 60.0 then $"1 ヶ月前"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} ヶ月前"
            elif totalDays < 730.5 then $"1 年前"
            else $"{Math.Floor(totalDays / 365.25):F0} 年前"
        | "ru" -> (* Russian *)
            if totalSeconds < 2.0 then $"1 секунду назад"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} секунд назад"
            elif totalMinutes < 2.0 then $"1 минуту назад"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} минут назад"
            elif totalHours < 2.0 then $"1 час назад"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} часов назад"
            elif totalDays < 2.0 then $"1 день назад"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} дней назад"
            elif totalDays < 60.0 then $"1 месяц назад"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} месяцев назад"
            elif totalDays < 730.5 then $"1 год назад"
            else $"{Math.Floor(totalDays / 365.25):F0} лет назад"
        | "uk" -> (* Ukrainian *)
            if totalSeconds < 2.0 then $"1 секунду тому"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} секунд тому"
            elif totalMinutes < 2.0 then $"1 хвилину тому"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} хвилин тому"
            elif totalHours < 2.0 then $"1 годину тому"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} годин тому"
            elif totalDays < 2.0 then $"1 день тому"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} днів тому"
            elif totalDays < 60.0 then $"1 місяць тому"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} місяців тому"
            elif totalDays < 730.5 then $"1 рік тому"
            else $"{Math.Floor(totalDays / 365.25):F0} років тому"
        | "hu" -> (* Hungarian *)
            if totalSeconds < 2.0 then $"1 másodperce"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} másodperce"
            elif totalMinutes < 2.0 then $"1 perce"
            elif totalMinutes < 60.0 then $"${Math.Floor(totalMinutes):F0} perce"
            elif totalHours < 2.0 then $"1 órája"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} órája"
            elif totalDays < 2.0 then $"1 napja"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} napja"
            elif totalDays < 60.0 then $"1 hónapja"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} hónapja"
            elif totalDays < 730.5 then $"1 éve"
            else $"${Math.Floor(totalDays / 365.25):F0} éve"
        | "pt" -> (* Portuguese *)
            if totalSeconds < 2.0 then $"1 segundo atrás"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} segundos atrás"
            elif totalMinutes < 2.0 then $"1 minuto atrás"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} minutos atrás"
            elif totalHours < 2.0 then $"1 hora atrás"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} horas atrás"
            elif totalDays < 2.0 then $"1 dia atrás"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} dias atrás"
            elif totalDays < 60.0 then $"1 mês atrás"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} meses atrás"
            elif totalDays < 730.5 then $"1 ano atrás"
            else $"{Math.Floor(totalDays / 365.25):F0} anos atrás"
        | "it" -> (* Italian *)
            if totalSeconds < 2.0 then $"1 secondo fa"
            elif totalSeconds < 60.0 then $"fa {Math.Floor(totalSeconds):F0} secondi"
            elif totalMinutes < 2.0 then $"fa 1 minuto"
            elif totalMinutes < 60.0 then $"fa {Math.Floor(totalMinutes):F0} minuti"
            elif totalHours < 2.0 then $"fa 1 ora"
            elif totalHours < 24.0 then $"fa {Math.Floor(totalHours):F0} ore"
            elif totalDays < 2.0 then $"fa 1 giorno"
            elif totalDays < 30.0 then $"fa {Math.Floor(totalDays):F0} giorni"
            elif totalDays < 60.0 then $"fa 1 mese"
            elif totalDays < 365.25 then $"fa {Math.Floor(totalDays / 30.0):F0} mesi"
            elif totalDays < 730.5 then $"fa 1 anno"
            else $"fa {Math.Floor(totalDays / 365.25):F0} anni"
        | "es" -> (* Spanish *)
            if totalSeconds < 2.0 then $"hace 1 segundo"
            elif totalSeconds < 60.0 then $"hace {Math.Floor(totalSeconds):F0} segundos"
            elif totalMinutes < 2.0 then $"hace 1 minuto"
            elif totalMinutes < 60.0 then $"hace {Math.Floor(totalMinutes):F0} minutos"
            elif totalHours < 2.0 then $"hace 1 hora"
            elif totalHours < 24.0 then $"hace {Math.Floor(totalHours):F0} horas"
            elif totalDays < 2.0 then $"hace 1 día"
            elif totalDays < 30.0 then $"hace {Math.Floor(totalDays):F0} días"
            elif totalDays < 60.0 then $"hace 1 mes"
            elif totalDays < 365.25 then $"hace {Math.Floor(totalDays / 30.0):F0} meses"
            elif totalDays < 730.5 then $"hace 1 año"
            else $"hace {Math.Floor(totalDays / 365.25):F0} años"
        | "de" -> (* German *)
            if totalSeconds < 2.0 then $"vor 1 Sekunde"
            elif totalSeconds < 60.0 then $"vor {Math.Floor(totalSeconds):F0} Sekunden"
            elif totalMinutes < 2.0 then $"vor 1 Minute"
            elif totalMinutes < 60.0 then $"vor {Math.Floor(totalMinutes):F0} Minuten"
            elif totalHours < 2.0 then $"vor 1 Stunde"
            elif totalHours < 24.0 then $"vor {Math.Floor(totalHours):F0} Stunden"
            elif totalDays < 2.0 then $"vor 1 Tag"
            elif totalDays < 30.0 then $"vor {Math.Floor(totalDays):F0} Tagen"
            elif totalDays < 60.0 then $"vor 1 Monat"
            elif totalDays < 365.25 then $"vor {Math.Floor(totalDays / 30.0):F0} Monaten"
            elif totalDays < 730.5 then $"vor 1 Jahr"
            else $"vor {Math.Floor(totalDays / 365.25):F0} Jahren"
        | "fr" -> (* French *)
            if totalSeconds < 2.0 then $"il y a 1 seconde"
            elif totalSeconds < 60.0 then $"il y a {Math.Floor(totalSeconds):F0} secondes"
            elif totalMinutes < 2.0 then $"il y a 1 minute"
            elif totalMinutes < 60.0 then $"il y a {Math.Floor(totalMinutes):F0} minutes"
            elif totalHours < 2.0 then $"il y a 1 heure"
            elif totalHours < 24.0 then $"il y a {Math.Floor(totalHours):F0} heures"
            elif totalDays < 2.0 then $"il y a 1 jour"
            elif totalDays < 30.0 then $"il y a {Math.Floor(totalDays):F0} jours"
            elif totalDays < 60.0 then $"il y a 1 mois"
            elif totalDays < 365.25 then $"il y a {Math.Floor(totalDays / 30.0):F0} mois"
            elif totalDays < 730.5 then $"il y a 1 an"
            else $"il y a {Math.Floor(totalDays / 365.25):F0} ans"
        | _ -> (* English by default *)
            if totalSeconds < 2.0 then $"1 second ago"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} seconds ago"
            elif totalMinutes < 2.0 then $"1 minute ago"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} minutes ago"
            elif totalHours < 2.0 then $"1 hour ago"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} hours ago"
            elif totalDays < 2.0 then $"1 day ago"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} days ago"
            elif totalDays < 60.0 then $"1 month ago"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} months ago"
            elif totalDays < 730.5 then $"1 year ago"
            else $"{Math.Floor(totalDays / 365.25):F0} years ago"

    /// Computes text for how far apart two instants are.
    let apart language (instant1: Instant) (instant2: Instant) =
        let since = if instant2 > instant1 then instant2.Minus(instant1) else instant1.Minus(instant2)
        
        let totalSeconds = since.TotalSeconds
        let totalMinutes = since.TotalMinutes
        let totalHours = since.TotalHours
        let totalDays = since.TotalDays
        
        match language with
        | "en" -> (* English *)
            if totalSeconds < 2.0 then $"1 second apart"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} seconds apart"
            elif totalMinutes < 2.0 then $"1 minute apart"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} minutes apart"
            elif totalHours < 2.0 then $"1 hour apart"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} hours apart"
            elif totalDays < 2.0 then $"1 day apart"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} days apart"
            elif totalDays < 60.0 then $"1 month apart"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} months apart"
            elif totalDays < 730.5 then $"1 year apart"
            else $"{Math.Floor(totalDays / 365.25):F0} years apart"
        | "es" -> (* Spanish *)
            if totalSeconds < 2.0 then "1 segundo de diferencia"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} segundos de diferencia"
            elif totalMinutes < 2.0 then "1 minuto de diferencia"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} minutos de diferencia"
            elif totalHours < 2.0 then "1 hora de diferencia"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} horas de diferencia"
            elif totalDays < 2.0 then "1 día de diferencia"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} días de diferencia"
            elif totalDays < 60.0 then "1 mes de diferencia"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} meses de diferencia"
            elif totalDays < 730.5 then "1 año de diferencia"
            else $"{Math.Floor(totalDays / 365.25):F0} años de diferencia"
        | "pt" -> (* Portuguese *)
            if totalSeconds < 2.0 then "1 segundo de diferença"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} segundos de diferença"
            elif totalMinutes < 2.0 then "1 minuto de diferença"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} minutos de diferença"
            elif totalHours < 2.0 then "1 hora de diferença"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} horas de diferença"
            elif totalDays < 2.0 then "1 dia de diferença"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} dias de diferença"
            elif totalDays < 60.0 then "1 mês de diferença"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} meses de diferença"
            elif totalDays < 730.5 then "1 ano de diferença"
            else $"{Math.Floor(totalDays / 365.25):F0} anos de diferença"
        | "de" -> (* German *)
            if totalSeconds < 2.0 then "1 Sekunde auseinander"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} Sekunden auseinander"
            elif totalMinutes < 2.0 then "1 Minute auseinander"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} Minuten auseinander"
            elif totalHours < 2.0 then "1 Stunde auseinander"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} Stunden auseinander"
            elif totalDays < 2.0 then "1 Tag auseinander"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} Tage auseinander"
            elif totalDays < 60.0 then "1 Monat auseinander"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} Monate auseinander"
            elif totalDays < 730.5 then "1 Jahr auseinander"
            else $"{Math.Floor(totalDays / 365.25):F0} Jahre auseinander"            
        | "fr" -> (* French *)
            if totalSeconds < 2.0 then "1 seconde d'écart"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} secondes d'écart"
            elif totalMinutes < 2.0 then "1 minute d'écart"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} minutes d'écart"
            elif totalHours < 2.0 then "1 heure d'écart"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} heures d'écart"
            elif totalDays < 2.0 then "1 jour d'écart"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} jours d'écart"
            elif totalDays < 60.0 then "1 mois d'écart"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} mois d'écart"
            elif totalDays < 730.5 then "1 an d'écart"
            else $"{Math.Floor(totalDays / 365.25):F0} ans d'écart"
        | "nl" -> // Dutch
            if totalSeconds < 2.0 then "1 seconde verschil"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} seconden verschil"
            elif totalMinutes < 2.0 then "1 minuut verschil"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} minuten verschil"
            elif totalHours < 2.0 then "1 uur verschil"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} uur verschil"
            elif totalDays < 2.0 then "1 dag verschil"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} dagen verschil"
            elif totalDays < 60.0 then "1 maand verschil"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} maanden verschil"
            elif totalDays < 730.5 then "1 jaar verschil"
            else $"{Math.Floor(totalDays / 365.25):F0} jaar verschil"
        | "it" -> // Italian
            if totalSeconds < 2.0 then "1 secondo di differenza"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} secondi di differenza"
            elif totalMinutes < 2.0 then "1 minuto di differenza"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} minuti di differenza"
            elif totalHours < 2.0 then "1 ora di differenza"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} ore di differenza"
            elif totalDays < 2.0 then "1 giorno di differenza"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} giorni di differenza"
            elif totalDays < 60.0 then "1 mese di differenza"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} mesi di differenza"
            elif totalDays < 730.5 then "1 anno di differenza"
            else $"{Math.Floor(totalDays / 365.25):F0} anni di differenza"
        | "hu" -> // Hungarian
            if totalSeconds < 2.0 then "1 másodperc különbség"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} másodperc különbség"
            elif totalMinutes < 2.0 then "1 perc különbség"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} perc különbség"
            elif totalHours < 2.0 then "1 óra különbség"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} óra különbség"
            elif totalDays < 2.0 then "1 nap különbség"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} nap különbség"
            elif totalDays < 60.0 then "1 hónap különbség"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} hónap különbség"
            elif totalDays < 730.5 then "1 év különbség"
            else $"{Math.Floor(totalDays / 365.25):F0} év különbség"
        | "cs" -> // Czech
            if totalSeconds < 2.0 then "1 sekunda rozdíl"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} sekund rozdíl"
            elif totalMinutes < 2.0 then "1 minuta rozdíl"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} minut rozdíl"
            elif totalHours < 2.0 then "1 hodina rozdíl"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} hodin rozdíl"
            elif totalDays < 2.0 then "1 den rozdíl"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} dnů rozdíl"
            elif totalDays < 60.0 then "1 měsíc rozdíl"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} měsíců rozdíl"
            elif totalDays < 730.5 then "1 rok rozdíl"
            else $"{Math.Floor(totalDays / 365.25):F0} let rozdíl"
        | "uk" -> // Ukrainian
            if totalSeconds < 2.0 then "1 секунда різниці"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} секунд різниці"
            elif totalMinutes < 2.0 then "1 хвилина різниці"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} хвилин різниці"
            elif totalHours < 2.0 then "1 година різниці"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} годин різниці"
            elif totalDays < 2.0 then "1 день різниці"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} днів різниці"
            elif totalDays < 60.0 then "1 місяць різниці"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} місяців різниці"
            elif totalDays < 730.5 then "1 рік різниці"
            else $"{Math.Floor(totalDays / 365.25):F0} років різниці"
        | "ru" -> // Russian
            if totalSeconds < 2.0 then "1 секунда разницы"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} секунд разницы"
            elif totalMinutes < 2.0 then "1 минута разницы"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} минут разницы"
            elif totalHours < 2.0 then "1 час разницы"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} часов разницы"
            elif totalDays < 2.0 then "1 день разницы"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} дней разницы"
            elif totalDays < 60.0 then "1 месяц разницы"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} месяцев разницы"
            elif totalDays < 730.5 then "1 год разницы"
            else $"{Math.Floor(totalDays / 365.25):F0} лет разницы"
        | "ja" -> // Japanese
            if totalSeconds < 2.0 then "1秒の差"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0}秒の差"
            elif totalMinutes < 2.0 then "1分の差"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0}分の差"
            elif totalHours < 2.0 then "1時間の差"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0}時間の差"
            elif totalDays < 2.0 then "1日の差"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0}日の差"
            elif totalDays < 60.0 then "1ヶ月の差"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0}ヶ月の差"
            elif totalDays < 730.5 then "1年の差"
            else $"{Math.Floor(totalDays / 365.25):F0}年の差"
        | "zh" -> // Chinese
            if totalSeconds < 2.0 then "相差1秒"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0}秒相差"
            elif totalMinutes < 2.0 then "相差1分钟"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0}分钟相差"
            elif totalHours < 2.0 then "相差1小时"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0}小时相差"
            elif totalDays < 2.0 then "相差1天"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0}天相差"
            elif totalDays < 60.0 then "相差1个月"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0}个月相差"
            elif totalDays < 730.5 then "相差1年"
            else $"{Math.Floor(totalDays / 365.25):F0}年相差"
        | _ -> (* English by default *)
            if totalSeconds < 2.0 then $"1 second apart"
            elif totalSeconds < 60.0 then $"{Math.Floor(totalSeconds):F0} seconds apart"
            elif totalMinutes < 2.0 then $"1 minute apart"
            elif totalMinutes < 60.0 then $"{Math.Floor(totalMinutes):F0} minutes apart"
            elif totalHours < 2.0 then $"1 hour apart"
            elif totalHours < 24.0 then $"{Math.Floor(totalHours):F0} hours apart"
            elif totalDays < 2.0 then $"1 day apart"
            elif totalDays < 30.0 then $"{Math.Floor(totalDays):F0} days apart"
            elif totalDays < 60.0 then $"1 month apart"
            elif totalDays < 365.25 then $"{Math.Floor(totalDays / 30.0):F0} months apart"
            elif totalDays < 730.5 then $"1 year apart"
            else $"{Math.Floor(totalDays / 365.25):F0} years apart"

    /// This is intended to be the definitive list of locali[sz]ations that Grace supports.
    ///
    /// I'm starting with en-US, because that's all I know, but trying to do it right from the start.
    type Language =
        | ``EN-US``

    type StringResourceName =
        | BranchAlreadyExists
        | BranchDoesNotExist
        | BranchIdIsRequired
        | BranchIdsAreRequired
        | BranchIsNotBasedOnLatestPromotion
        | BranchNameAlreadyExists
        | BranchNameIsRequired
        | CheckpointIsDisabled
        | CommitIsDisabled
        | CreatingNewDirectoryVersions
        | CreatingSaveReference
        | DeleteReasonIsRequired
        | DescriptionIsRequired
        | DirectoryAlreadyExists
        | DirectoryDoesNotExist
        | DuplicateCorrelationId
        | EitherBranchIdOrBranchNameIsRequired
        | EitherOrganizationIdOrOrganizationNameIsRequired
        | EitherOwnerIdOrOwnerNameIsRequired
        | EitherRepositoryIdOrRepositoryNameIsRequired
        | ExceptionCaught
        | FailedCommunicatingWithObjectStorage
        | FailedCreatingInitialBranch
        | FailedRebasingInitialBranch
        | FailedCreatingInitialPromotion
        | FailedToGetUploadUrls
        | FailedToRetrieveBranch
        | FailedUploadingFilesToObjectStorage
        | FailedWhileApplyingEvent
        | FailedWhileSavingEvent
        | FilesMustNotBeEmpty
        | GettingLatestVersion
        | GettingCurrentBranch
        | GraceConfigFileNotFound
        | IndexFileNotFound
        | InitialPromotionMessage
        | InterprocessFileDeleted
        | InvalidBranchId
        | InvalidBranchName
        | InvalidCheckpointDaysValue
        | InvalidDirectoryPath
        | InvalidDirectoryId
        | InvalidMaxCountValue
        | InvalidObjectStorageProvider
        | InvalidOrganizationId
        | InvalidOrganizationName
        | InvalidOrganizationType
        | InvalidOwnerId
        | InvalidOwnerName
        | InvalidOwnerType
        | InvalidReferenceType
        | InvalidRepositoryId
        | InvalidRepositoryName
        | InvalidRepositoryStatus
        | InvalidSaveDaysValue
        | InvalidSearchVisibility
        | InvalidServerApiVersion
        | InvalidSha256Hash
        | InvalidSize
        | InvalidVisibilityValue
        | PromotionIsDisabled
        | PromotionNotAvailableBecauseThereAreNoPromotableReferences
        | MessageIsRequired
        | NotImplemented
        | ObjectCacheFileNotFound
        | ObjectStorageException
        | OrganizationIdAlreadyExists
        | OrganizationNameAlreadyExists
        | OrganizationContainsRepositories
        | OrganizationDoesNotExist
        | OrganizationIdDoesNotExist
        | OrganizationIdIsRequired
        | OrganizationIsDeleted
        | OrganizationIsNotDeleted
        | OrganizationNameIsRequired
        | OrganizationTypeIsRequired
        | OwnerContainsOrganizations
        | OwnerDoesNotExist
        | OwnerIdDoesNotExist
        | OwnerIdAlreadyExists
        | OwnerIdIsRequired
        | OwnerIsDeleted
        | OwnerIsNotDeleted
        | OwnerNameAlreadyExists
        | OwnerNameIsRequired
        | OwnerTypeIsRequired
        | ParentBranchDoesNotExist
        | ReadingGraceStatus
        | ReferenceIdDoesNotExist
        | ReferenceIdsAreRequired
        | ReferenceTypeMustBeProvided
        | RelativePathMustNotBeEmpty
        | RepositoryContainsBranches
        | RepositoryDoesNotExist
        | RepositoryIdAlreadyExists
        | RepositoryIdDoesNotExist
        | RepositoryIdIsRequired
        | RepositoryIsAlreadyInitialized
        | RepositoryIsDeleted
        | RepositoryIsNotDeleted
        | RepositoryIsNotEmpty
        | RepositoryNameIsRequired
        | RepositoryNameAlreadyExists
        | SaveIsDisabled
        | SavingDirectoryVersions
        | ScanningWorkingDirectory
        | SearchVisibilityIsRequired
        | ServerRequestsMustIncludeXCorrelationIdHeader
        | Sha256HashDoesNotExist
        | Sha256HashIsRequired
        | StringIsTooLong
        | TagIsDisabled
        | UpdatingWorkingDirectory
        | UploadingFiles
        | ValueMustBePositive
