package com.kch.grassmanager

import android.content.Context
import java.io.IOException
import java.net.HttpURLConnection
import java.net.SocketTimeoutException
import java.net.URL
import java.net.URLEncoder
import java.net.UnknownHostException
import java.time.LocalDate

/** 하루치 잔디: 색 단계(0~4)와 기여 수 */
data class Day(val level: Int, val count: Int)

/** GitHub 공개 잔디 페이지 읽기 (PC 위젯 GitHub.Fetch와 같은 방식, 토큰 필요 없음) */
object Grass {
    private val cellRe = Regex("<td[^>]*\\bdata-date=\"[^>]*>")
    private val tipRe = Regex("<tool-tip[^>]*\\bfor=\"([^\"]+)\"[^>]*>([^<]*)</tool-tip>")
    private val numRe = Regex("^\\d[\\d,]*")

    private fun attr(tag: String, name: String): String? =
        Regex("\\b$name=\"([^\"]*)\"").find(tag)?.groupValues?.get(1)

    // 기본 페이지는 최근 1년이지만 UTC 기준 '오늘'까지만 있다. 한국은 자정~오전 9시에 오늘 칸이 아직 없으므로
    // 그때는 오늘이 든 해의 페이지(?to=오늘, 1/1~12/31)를 한 번 더 받아 합친다.
    fun fetch(user: String): Map<LocalDate, Day> {
        val url = "https://github.com/users/" + URLEncoder.encode(user, "UTF-8") + "/contributions"
        val days = parse(download(url)).toMutableMap()
        val today = LocalDate.now()
        if (today !in days) days.putAll(parse(download("$url?to=$today")))
        return days
    }

    private fun download(url: String): String {
        val con = URL(url).openConnection() as HttpURLConnection
        try {
            con.connectTimeout = 10_000
            con.readTimeout = 15_000
            con.setRequestProperty("User-Agent", "GrassManagerAndroid")
            val code = con.responseCode
            if (code == 404) throw IOException("아이디를 찾을 수 없음")
            if (code != 200) throw IOException("GitHub 응답 $code")
            return con.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }
        } finally {
            con.disconnect()
        }
    }

    private fun parse(html: String): Map<LocalDate, Day> {
        // 툴팁 문장("3 contributions on …", "No contributions on …")에서 칸 id별 기여 수
        val counts = HashMap<String, Int>()
        for (m in tipRe.findAll(html)) {
            val num = numRe.find(m.groupValues[2].trim())
            counts[m.groupValues[1]] = num?.value?.replace(",", "")?.toIntOrNull() ?: 0
        }
        val days = HashMap<LocalDate, Day>()
        for (m in cellRe.findAll(html)) {
            val date = runCatching { LocalDate.parse(attr(m.value, "data-date")) }.getOrNull() ?: continue
            val level = attr(m.value, "data-level")?.toIntOrNull() ?: 0
            val count = attr(m.value, "id")?.let { counts[it] } ?: if (level > 0) 1 else 0
            days[date] = Day(level, count)
        }
        if (days.isEmpty()) throw IOException("잔디 칸을 찾지 못함")
        return days
    }

    /** 위젯에 보여 줄 짧은 오류 문구 */
    fun describe(e: Throwable): String = when (e) {
        is UnknownHostException -> "인터넷 연결 없음"
        is SocketTimeoutException -> "응답 없음"
        is IOException -> e.message ?: "불러오지 못함"
        else -> "불러오지 못함"
    }

    /** 연속 기록: 오늘 아직 없으면 어제부터 센다 */
    fun streak(days: Map<LocalDate, Day>, today: LocalDate = LocalDate.now()): Int {
        var d = today
        if ((days[d]?.count ?: 0) == 0) d = d.minusDays(1)
        var n = 0
        while ((days[d]?.count ?: 0) > 0) { n++; d = d.minusDays(1) }
        return n
    }
}

/** 아이디·마지막으로 받은 잔디·오류를 휴대폰에 저장 (위젯을 다시 그릴 때 인터넷 없이 쓰려고) */
class Store(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences("grass", Context.MODE_PRIVATE)

    var user: String
        get() = prefs.getString("user", null)?.takeIf { it.isNotBlank() } ?: DEFAULT_USER
        set(v) = prefs.edit().putString("user", v.trim()).apply()

    var error: String?
        get() = prefs.getString("error", null)
        set(v) = prefs.edit().putString("error", v).apply()

    val lastOk: Long get() = prefs.getLong("lastOk", 0L)

    /** "2026-11-15:2:5;…" 형태로 저장한 날짜별 단계·기여 수 */
    var days: Map<LocalDate, Day>
        get() {
            val raw = prefs.getString("days", null) ?: return emptyMap()
            val map = HashMap<LocalDate, Day>()
            for (item in raw.split(';')) {
                val p = item.split(':')
                if (p.size != 3) continue
                val d = runCatching { LocalDate.parse(p[0]) }.getOrNull() ?: continue
                map[d] = Day(p[1].toIntOrNull() ?: 0, p[2].toIntOrNull() ?: 0)
            }
            return map
        }
        set(v) {
            val raw = v.entries.joinToString(";") { "${it.key}:${it.value.level}:${it.value.count}" }
            prefs.edit().putString("days", raw).putLong("lastOk", System.currentTimeMillis()).apply()
        }

    /** 아이디를 바꾸면 이전 사람의 잔디를 지운다 */
    fun clearData() = prefs.edit().remove("days").remove("lastOk").remove("error").apply()

    companion object {
        const val DEFAULT_USER = "Kangnagi"
    }
}
