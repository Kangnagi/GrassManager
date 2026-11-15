package com.kch.grassmanager

import android.app.AlarmManager
import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.util.TypedValue
import android.view.View
import android.widget.RemoteViews
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread

/**
 * 홈 화면 위젯 (기본 4×2). 1×1~4×2 크기별 위젯이 모두 이 클래스를 이어받아 같은 코드를 쓴다.
 * 어떤 위젯이든 길게 눌러 크기를 바꿀 수 있고, 그리는 모양은 그때그때의 실제 크기로 고른다.
 * 안드로이드가 30분마다(updatePeriodMillis) onUpdate를 불러 주고,
 * 새로고침 버튼·자정 알람·앱의 설정 저장은 ACTION_REFRESH 방송으로 들어온다. 한 번 받으면 모든 위젯을 다시 그린다.
 */
open class GrassWidgetProvider : AppWidgetProvider() {

    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) = refresh(context)

    override fun onReceive(context: Context, intent: Intent) {
        super.onReceive(context, intent)
        if (intent.action == ACTION_REFRESH) refresh(context)
    }

    // 위젯 크기를 바꾸면 그 크기에 맞는 모양으로 다시 그리기만 한다
    override fun onAppWidgetOptionsChanged(context: Context, manager: AppWidgetManager, id: Int, options: Bundle) =
        render(context)

    override fun onDisabled(context: Context) {
        // 모든 위젯이 없어지면 자정 알람도 끈다
        if (PROVIDERS.all { ids(context, it).isEmpty() })
            context.getSystemService(AlarmManager::class.java).cancel(refreshIntent(context))
    }

    /** 잔디를 다시 받아 그린다. 방송 처리 시간(약 10초) 동안 goAsync로 기다린다 */
    private fun refresh(context: Context) {
        if (!busy.compareAndSet(false, true)) return
        val pending = goAsync()
        render(context)
        thread {
            val store = Store(context)
            try {
                store.days = Grass.fetch(store.user)
                store.error = null
            } catch (e: Exception) {
                store.error = Grass.describe(e)
            } finally {
                busy.set(false)
                render(context)
                pending.finish()
            }
        }
    }

    /** 위젯 모양: 1×1, 2×1, 2×2, 한 줄(3·4×1), 전체(3·4×2) */
    private enum class Kind(val layout: Int) {
        TINY(R.layout.widget_grass_tiny),
        WIDE(R.layout.widget_grass_wide),
        SQUARE(R.layout.widget_grass_square),
        ROW(R.layout.widget_grass_small),
        FULL(R.layout.widget_grass),
    }

    companion object {
        const val ACTION_REFRESH = "com.kch.grassmanager.REFRESH"
        const val HOME_URL = "https://kangnagi.github.io/GrassManager/"

        /** 크기별 위젯 클래스와 처음 크기(가로칸, 세로칸) — 크기 정보가 아직 없을 때 쓴다 */
        private val PROVIDERS = listOf(
            GrassWidget1x1Provider::class.java, GrassWidget2x1Provider::class.java, GrassWidget2x2Provider::class.java,
            GrassWidget3x1Provider::class.java, GrassWidget3x2Provider::class.java,
            GrassWidgetSmallProvider::class.java, GrassWidgetProvider::class.java,
        )
        private val DEFAULT_CELLS = listOf(1 to 1, 2 to 1, 2 to 2, 3 to 1, 3 to 2, 4 to 1, 4 to 2)

        private val busy = AtomicBoolean(false)
        private val frameIds = intArrayOf(R.id.frame0, R.id.frame1, R.id.frame2, R.id.frame3, R.id.frame4, R.id.frame5, R.id.frame6)
        private val cellIds = intArrayOf(R.id.cell0, R.id.cell1, R.id.cell2, R.id.cell3, R.id.cell4, R.id.cell5, R.id.cell6)
        private val ringIds = intArrayOf(R.id.ring0, R.id.ring1, R.id.ring2, R.id.ring3, R.id.ring4, R.id.ring5, R.id.ring6)
        private val countIds = intArrayOf(R.id.count0, R.id.count1, R.id.count2, R.id.count3, R.id.count4, R.id.count5, R.id.count6)
        private val dayIds = intArrayOf(R.id.day0, R.id.day1, R.id.day2, R.id.day3, R.id.day4, R.id.day5, R.id.day6)
        private val levelCells = intArrayOf(R.drawable.cell_l0, R.drawable.cell_l1, R.drawable.cell_l2, R.drawable.cell_l3, R.drawable.cell_l4)

        /** 앱 화면에서 위젯 새로고침을 요청할 때 */
        fun requestRefresh(context: Context) = context.sendBroadcast(refreshBroadcast(context))

        private fun refreshBroadcast(context: Context) =
            Intent(context, GrassWidgetProvider::class.java).setAction(ACTION_REFRESH)

        private fun refreshIntent(context: Context): PendingIntent =
            PendingIntent.getBroadcast(context, 0, refreshBroadcast(context),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)

        private fun ids(context: Context, cls: Class<*>): IntArray =
            AppWidgetManager.getInstance(context).getAppWidgetIds(ComponentName(context, cls))

        /** 저장된 잔디로 모든 위젯을 다시 그린다 (인터넷 사용 없음). 위젯마다 크기가 달라 하나씩 그린다 */
        fun render(context: Context) {
            val manager = AppWidgetManager.getInstance(context)
            val data = Snapshot(Store(context))
            var any = false
            PROVIDERS.forEachIndexed { i, cls ->
                for (id in ids(context, cls)) {
                    any = true
                    val (cols, rows) = DEFAULT_CELLS[i]
                    val o = manager.getAppWidgetOptions(id)
                    // 세로 화면 기준으로 너비는 최소값, 높이는 최대값이 실제 크기(dp)다
                    val w = o.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH).takeIf { it > 0 } ?: (cols * 89)
                    val h = o.getInt(AppWidgetManager.OPTION_APPWIDGET_MAX_HEIGHT).takeIf { it > 0 } ?: (rows * 100)
                    manager.updateAppWidget(id, build(context, data, w.toFloat(), h.toFloat()))
                }
            }
            if (any) scheduleMidnight(context)
        }

        // 자정이 지나면 오늘 칸이 바뀌므로 자정 직후에 한 번 새로고침 (정확한 알람은 권한이 필요해 대략적인 알람 사용)
        private fun scheduleMidnight(context: Context) {
            val next = LocalDate.now().plusDays(1).atTime(LocalTime.of(0, 1))
                .atZone(ZoneId.systemDefault()).toInstant().toEpochMilli()
            context.getSystemService(AlarmManager::class.java)
                .set(AlarmManager.RTC, next, refreshIntent(context))
        }

        /** 크기(dp)로 모양 고르기: 홈 화면 한 칸은 대략 가로 90dp, 세로 100dp */
        private fun kindOf(w: Float, h: Float): Kind {
            val twoRows = h >= 150
            return when {
                w < 130 -> Kind.TINY
                w < 220 -> if (twoRows) Kind.SQUARE else Kind.WIDE
                else -> if (twoRows) Kind.FULL else Kind.ROW
            }
        }

        /** 한 번 그릴 때 쓰는 저장값 (위젯마다 다시 읽지 않도록) */
        private class Snapshot(store: Store) {
            val days = store.days
            val error = store.error
            val loaded = days.isNotEmpty()
            val today: LocalDate = LocalDate.now()
            val todayDay = days[today] ?: Day(0, 0)
        }

        // 글자색은 색 리소스로 지정해야 휴대폰 다크/라이트가 바뀔 때 다시 받지 않아도 따라 바뀐다 (안드로이드 12+)
        private fun RemoteViews.textColor(context: Context, id: Int, colorRes: Int) {
            if (Build.VERSION.SDK_INT >= 31) setColorStateList(id, "setTextColor", colorRes)
            else setTextColor(id, context.getColor(colorRes))
        }

        // 뷰 크기 바꾸기는 안드로이드 12+에서만 된다 (그 아래는 레이아웃에 적힌 기본 크기)
        private fun RemoteViews.square(id: Int, dp: Float) {
            if (Build.VERSION.SDK_INT < 31) return
            setViewLayoutWidth(id, dp, TypedValue.COMPLEX_UNIT_DIP)
            setViewLayoutHeight(id, dp, TypedValue.COMPLEX_UNIT_DIP)
        }

        private fun RemoteViews.textSp(id: Int, sp: Float) = setTextViewTextSize(id, TypedValue.COMPLEX_UNIT_SP, sp)

        private fun countText(n: Int) = if (n > 99) "99+" else n.toString()

        private fun build(context: Context, data: Snapshot, w: Float, h: Float): RemoteViews {
            val kind = kindOf(w, h)
            val v = RemoteViews(context.packageName, kind.layout)

            // 위젯을 누르면 홈페이지, 새로고침 아이콘은 다시 읽기 (1×1에는 새로고침 아이콘 없음)
            v.setOnClickPendingIntent(R.id.widget_root, PendingIntent.getActivity(context, 1,
                Intent(Intent.ACTION_VIEW, Uri.parse(HOME_URL)), PendingIntent.FLAG_IMMUTABLE))
            if (kind != Kind.TINY) v.setOnClickPendingIntent(R.id.refresh, refreshIntent(context))

            when (kind) {
                Kind.TINY, Kind.WIDE, Kind.SQUARE -> buildCompact(context, v, kind, data, w, h)
                Kind.ROW, Kind.FULL -> buildWeek(context, v, kind == Kind.ROW, data, w, h)
            }
            return v
        }

        /** 1×1·2×1·2×2: 오늘 칸을 크게 (+ 2×2는 이번 주 작은 7칸) */
        private fun buildCompact(context: Context, v: RemoteViews, kind: Kind, data: Snapshot, w: Float, h: Float) {
            val t = data.todayDay.count
            val streak = if (data.loaded) Grass.streak(data.days, data.today) else 0

            // 오늘 칸 크기: 남는 공간에서 가장 크게
            var mini = 0f
            val cell = when (kind) {
                Kind.TINY -> minOf(w - 14, h - 12 - 26)
                Kind.WIDE -> minOf(h - 22, (w - 22) * 0.36f)
                else -> {
                    mini = ((w - 20) / 7 * 0.8f).coerceIn(10f, 26f)
                    minOf(h - 22 - 28 - mini - 8, (w - 22) * 0.42f)
                }
            }.coerceIn(26f, 76f)
            v.square(R.id.today_frame, cell)
            v.setImageViewResource(R.id.today_cell, levelCells[data.todayDay.level.coerceIn(0, 4)])
            v.setTextViewText(R.id.today_count, if (data.loaded) countText(t) else "")
            v.textSp(R.id.today_count, (cell * 0.42f).coerceIn(12f, 30f))
            v.textColor(context, R.id.today_count,
                if (data.todayDay.level >= 3) R.color.bg else if (t == 0) R.color.muted else R.color.fg)

            if (kind == Kind.TINY) {
                // 1×1: 연속 기록 글자색으로 오늘 커밋 여부 표시 (초록=함, 주황=아직)
                v.setTextViewText(R.id.streak, when {
                    busy.get() -> "…"
                    !data.loaded -> if (data.error != null) "오류" else "…"
                    else -> "연속 ${streak}일"
                })
                v.textColor(context, R.id.streak,
                    if (!data.loaded || data.error != null) R.color.muted else if (t > 0) R.color.ok else R.color.warn)
                return
            }

            // 2×1·2×2: 상태 글자
            val (status, color) = when {
                busy.get() -> "새로고침 중…" to R.color.muted
                !data.loaded -> (data.error ?: "불러오는 중…") to (if (data.error != null) R.color.warn else R.color.muted)
                data.error != null -> data.error to R.color.muted
                t > 0 -> "기여 완료" to R.color.ok
                else -> "아직 없음" to R.color.warn
            }
            v.setTextViewText(R.id.status, status)
            v.textColor(context, R.id.status, color)
            v.setTextViewText(R.id.streak, if (data.loaded) "연속 ${streak}일" else "")
            if (kind != Kind.SQUARE) return

            // 2×2: 이번 주 작은 7칸
            val sunday = data.today.minusDays((data.today.dayOfWeek.value % 7).toLong())
            for (i in 0 until 7) {
                val d = sunday.plusDays(i.toLong())
                val day = data.days[d] ?: Day(0, 0)
                v.square(frameIds[i], mini)
                v.setImageViewResource(cellIds[i],
                    if (d.isAfter(data.today)) R.drawable.cell_future else levelCells[day.level.coerceIn(0, 4)])
                v.setViewVisibility(ringIds[i], if (d == data.today) View.VISIBLE else View.GONE)
            }
        }

        /**
         * 3·4칸 너비: 일~토 7칸. 칸은 최대한 크게 — 가로는 7칸이 들어갈 너비, 세로는 제목·상태 줄을 뺀 높이 중 작은 쪽.
         * row=true면 한 줄짜리(오늘 상태가 위 줄), 아니면 전체(상태 줄이 아래 + 이번 주 활동일)
         */
        private fun buildWeek(context: Context, v: RemoteViews, row: Boolean, data: Snapshot, w: Float, h: Float) {
            val days = data.days
            val today = data.today
            val sunday = today.minusDays((today.dayOfWeek.value % 7).toLong())

            v.setTextViewText(R.id.streak, if (data.loaded) "연속 ${Grass.streak(days, today)}일" else "")

            val column = (w - 28) / 7f                          // 좌우 여백 28dp를 뺀 한 칸 자리
            val byWidth = column - maxOf(4f, column * 0.1f)     // 칸 사이 간격
            val chrome = if (row) 10 + 30 + 24 else 22 + 32 + 24 + 22  // 여백 + 제목 줄 + 요일 글자 (+ 상태 줄)
            val cell = if (Build.VERSION.SDK_INT >= 31) minOf(byWidth, h - chrome).coerceIn(22f, 64f) else 30f
            val countSp = (cell * 0.38f).coerceIn(10f, 18f)
            val daySp = (cell * 0.3f).coerceIn(10f, 13f)

            var active = 0
            for (i in 0 until 7) {
                val d = sunday.plusDays(i.toLong())
                val day = days[d] ?: Day(0, 0)
                val future = d.isAfter(today)
                val isToday = d == today
                if (!future && day.count > 0) active++
                v.square(frameIds[i], cell)
                v.textSp(countIds[i], countSp)
                v.textSp(dayIds[i], daySp)
                v.setImageViewResource(cellIds[i],
                    if (future) R.drawable.cell_future else levelCells[day.level.coerceIn(0, 4)])
                v.setViewVisibility(ringIds[i], if (isToday) View.VISIBLE else View.GONE)
                v.setTextViewText(countIds[i], if (!future && day.count > 0) countText(day.count) else "")
                v.textColor(context, countIds[i], if (day.level >= 3) R.color.bg else R.color.fg)
                v.textColor(context, dayIds[i], if (isToday) R.color.fg else R.color.muted)
            }

            val t = data.todayDay.count
            val (status, color) = when {
                busy.get() -> "새로고침 중…" to R.color.muted
                !data.loaded -> (data.error ?: "불러오는 중…") to (if (data.error != null) R.color.warn else R.color.muted)
                data.error != null -> ((if (t > 0) "오늘 ${t}개" else "오늘 아직 없음") + " · " + data.error) to R.color.muted
                t > 0 -> "오늘 ${t}개 기여 완료" to R.color.ok
                else -> "오늘 아직 커밋 없음" to R.color.warn
            }
            v.setTextViewText(R.id.status, status)
            v.textColor(context, R.id.status, color)
            if (!row) v.setTextViewText(R.id.week, if (data.loaded) "$active/7일" else "")
        }
    }
}

// 크기별 위젯: 그리기·새로고침은 모두 GrassWidgetProvider와 같고, 처음 놓을 때 크기만 다르다
/** 4×1 */
class GrassWidgetSmallProvider : GrassWidgetProvider()
/** 1×1 */
class GrassWidget1x1Provider : GrassWidgetProvider()
/** 2×1 */
class GrassWidget2x1Provider : GrassWidgetProvider()
/** 2×2 */
class GrassWidget2x2Provider : GrassWidgetProvider()
/** 3×1 */
class GrassWidget3x1Provider : GrassWidgetProvider()
/** 3×2 */
class GrassWidget3x2Provider : GrassWidgetProvider()
