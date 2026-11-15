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
 * 4×2 홈 화면 위젯. 안드로이드가 30분마다(updatePeriodMillis) onUpdate를 불러 주고,
 * 새로고침 버튼·자정 알람·앱의 설정 저장은 ACTION_REFRESH 방송으로 들어온다.
 * 4×1 위젯(GrassWidgetSmallProvider)도 같은 코드를 쓰고, 한쪽이 새로 받으면 둘 다 다시 그린다.
 */
open class GrassWidgetProvider : AppWidgetProvider() {

    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) = refresh(context)

    override fun onReceive(context: Context, intent: Intent) {
        super.onReceive(context, intent)
        if (intent.action == ACTION_REFRESH) refresh(context)
    }

    // 위젯 크기를 바꾸면 칸 크기를 다시 계산해 그리기만 한다
    override fun onAppWidgetOptionsChanged(context: Context, manager: AppWidgetManager, id: Int, options: Bundle) =
        render(context)

    override fun onDisabled(context: Context) {
        // 두 종류 위젯이 모두 없어지면 자정 알람도 끈다
        if (allIds(context).isEmpty())
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

    companion object {
        const val ACTION_REFRESH = "com.kch.grassmanager.REFRESH"
        const val HOME_URL = "https://kangnagi.github.io/GrassManager/"

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

        private fun allIds(context: Context) =
            ids(context, GrassWidgetProvider::class.java) + ids(context, GrassWidgetSmallProvider::class.java)

        /** 저장된 잔디로 모든 위젯(4×2, 4×1)을 다시 그린다 (인터넷 사용 없음). 위젯마다 크기가 달라 하나씩 그린다 */
        fun render(context: Context) {
            val manager = AppWidgetManager.getInstance(context)
            val big = ids(context, GrassWidgetProvider::class.java)
            val small = ids(context, GrassWidgetSmallProvider::class.java)
            if (big.isEmpty() && small.isEmpty()) return
            val data = Snapshot(Store(context))
            for (id in big) manager.updateAppWidget(id, build(context, data, false, manager.getAppWidgetOptions(id)))
            for (id in small) manager.updateAppWidget(id, build(context, data, true, manager.getAppWidgetOptions(id)))
            scheduleMidnight(context)
        }

        // 자정이 지나면 오늘 칸이 바뀌므로 자정 직후에 한 번 새로고침 (정확한 알람은 권한이 필요해 대략적인 알람 사용)
        private fun scheduleMidnight(context: Context) {
            val next = LocalDate.now().plusDays(1).atTime(LocalTime.of(0, 1))
                .atZone(ZoneId.systemDefault()).toInstant().toEpochMilli()
            context.getSystemService(AlarmManager::class.java)
                .set(AlarmManager.RTC, next, refreshIntent(context))
        }

        /** 한 번 그릴 때 쓰는 저장값 (위젯마다 다시 읽지 않도록) */
        private class Snapshot(store: Store) {
            val days = store.days
            val error = store.error
        }

        // 글자색은 색 리소스로 지정해야 휴대폰 다크/라이트가 바뀔 때 다시 받지 않아도 따라 바뀐다 (안드로이드 12+)
        private fun RemoteViews.textColor(context: Context, id: Int, colorRes: Int) {
            if (Build.VERSION.SDK_INT >= 31) setColorStateList(id, "setTextColor", colorRes)
            else setTextColor(id, context.getColor(colorRes))
        }

        /**
         * 위젯 크기(dp)에 맞춰 칸을 최대한 크게: 가로는 7칸이 들어갈 너비, 세로는 제목·상태 줄을 뺀 높이 중 작은 쪽.
         * 세로 화면 기준으로 너비는 최소값, 높이는 최대값이 실제 크기다.
         */
        private fun cellSize(options: Bundle, small: Boolean): Float {
            val w = options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH).takeIf { it > 0 } ?: 320
            val h = options.getInt(AppWidgetManager.OPTION_APPWIDGET_MAX_HEIGHT).takeIf { it > 0 } ?: if (small) 80 else 170
            val column = (w - 28) / 7f                          // 좌우 여백 28dp를 뺀 한 칸 자리
            val byWidth = column - maxOf(4f, column * 0.1f)     // 칸 사이 간격
            val chrome = if (small) 10 + 30 + 24 else 22 + 32 + 24 + 22  // 여백 + 제목 줄 + 요일 글자 (+ 상태 줄)
            val byHeight = h - chrome.toFloat()
            return minOf(byWidth, byHeight).coerceIn(22f, 64f)
        }

        private fun build(context: Context, data: Snapshot, small: Boolean, options: Bundle): RemoteViews {
            val days = data.days
            val error = data.error
            val loaded = days.isNotEmpty()
            val today = LocalDate.now()
            val sunday = today.minusDays((today.dayOfWeek.value % 7).toLong())
            val v = RemoteViews(context.packageName, if (small) R.layout.widget_grass_small else R.layout.widget_grass)

            // 위젯을 누르면 홈페이지, 새로고침 아이콘은 다시 읽기
            v.setOnClickPendingIntent(R.id.widget_root, PendingIntent.getActivity(context, 1,
                Intent(Intent.ACTION_VIEW, Uri.parse(HOME_URL)), PendingIntent.FLAG_IMMUTABLE))
            v.setOnClickPendingIntent(R.id.refresh, refreshIntent(context))

            v.setTextViewText(R.id.streak, if (loaded) "연속 ${Grass.streak(days, today)}일" else "")

            // 칸 크기는 안드로이드 12+에서만 바꿀 수 있다 (그 아래는 레이아웃의 30dp)
            val cell = if (Build.VERSION.SDK_INT >= 31) cellSize(options, small) else 30f
            val countSp = (cell * 0.38f).coerceIn(10f, 18f)
            val daySp = (cell * 0.3f).coerceIn(10f, 13f)

            // 일~토 7칸
            var active = 0
            for (i in 0 until 7) {
                val d = sunday.plusDays(i.toLong())
                val day = days[d] ?: Day(0, 0)
                val future = d.isAfter(today)
                val isToday = d == today
                if (!future && day.count > 0) active++
                if (Build.VERSION.SDK_INT >= 31) {
                    v.setViewLayoutWidth(frameIds[i], cell, TypedValue.COMPLEX_UNIT_DIP)
                    v.setViewLayoutHeight(frameIds[i], cell, TypedValue.COMPLEX_UNIT_DIP)
                }
                v.setTextViewTextSize(countIds[i], TypedValue.COMPLEX_UNIT_SP, countSp)
                v.setTextViewTextSize(dayIds[i], TypedValue.COMPLEX_UNIT_SP, daySp)
                v.setImageViewResource(cellIds[i],
                    if (future) R.drawable.cell_future else levelCells[day.level.coerceIn(0, 4)])
                v.setViewVisibility(ringIds[i], if (isToday) View.VISIBLE else View.GONE)
                v.setTextViewText(countIds[i],
                    if (!future && day.count > 0) (if (day.count > 99) "99+" else day.count.toString()) else "")
                v.textColor(context, countIds[i], if (day.level >= 3) R.color.bg else R.color.fg)
                v.textColor(context, dayIds[i], if (isToday) R.color.fg else R.color.muted)
            }

            // 상태: 오늘 기여 여부 (4×1은 위 줄, 4×2는 아래 줄 + 이번 주 활동일)
            val status: String
            val color: Int
            when {
                busy.get() -> { status = "새로고침 중…"; color = R.color.muted }
                !loaded -> { status = error ?: "불러오는 중…"; color = if (error != null) R.color.warn else R.color.muted }
                else -> {
                    val t = days[today]?.count ?: 0
                    if (error != null) { status = (if (t > 0) "오늘 ${t}개" else "오늘 아직 없음") + " · " + error; color = R.color.muted }
                    else if (t > 0) { status = "오늘 ${t}개 기여 완료"; color = R.color.ok }
                    else { status = "오늘 아직 커밋 없음"; color = R.color.warn }
                }
            }
            v.setTextViewText(R.id.status, status)
            v.textColor(context, R.id.status, color)
            if (!small) v.setTextViewText(R.id.week, if (loaded) "$active/7일" else "")
            return v
        }
    }
}

/** 4×1 위젯: 그리기·새로고침은 GrassWidgetProvider와 같고 레이아웃만 한 줄짜리 */
class GrassWidgetSmallProvider : GrassWidgetProvider()
