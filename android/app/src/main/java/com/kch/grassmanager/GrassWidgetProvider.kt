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
import android.view.View
import android.widget.RemoteViews
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread

/**
 * 홈 화면 위젯. 안드로이드가 30분마다(updatePeriodMillis) onUpdate를 불러 주고,
 * 새로고침 버튼·자정 알람·앱의 설정 저장은 ACTION_REFRESH 방송으로 들어온다.
 */
class GrassWidgetProvider : AppWidgetProvider() {

    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) = refresh(context)

    override fun onReceive(context: Context, intent: Intent) {
        super.onReceive(context, intent)
        if (intent.action == ACTION_REFRESH) refresh(context)
    }

    // 위젯 크기를 바꾸면 다시 그리기만 한다
    override fun onAppWidgetOptionsChanged(context: Context, manager: AppWidgetManager, id: Int, options: Bundle) =
        render(context)

    override fun onDisabled(context: Context) {
        // 마지막 위젯을 지우면 자정 알람도 끈다
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

        /** 저장된 잔디로 모든 위젯을 다시 그린다 (인터넷 사용 없음) */
        fun render(context: Context) {
            val manager = AppWidgetManager.getInstance(context)
            val ids = manager.getAppWidgetIds(ComponentName(context, GrassWidgetProvider::class.java))
            if (ids.isEmpty()) return
            manager.updateAppWidget(ids, build(context))
            scheduleMidnight(context)
        }

        // 자정이 지나면 오늘 칸이 바뀌므로 자정 직후에 한 번 새로고침 (정확한 알람은 권한이 필요해 대략적인 알람 사용)
        private fun scheduleMidnight(context: Context) {
            val next = LocalDate.now().plusDays(1).atTime(LocalTime.of(0, 1))
                .atZone(ZoneId.systemDefault()).toInstant().toEpochMilli()
            context.getSystemService(AlarmManager::class.java)
                .set(AlarmManager.RTC, next, refreshIntent(context))
        }

        // 글자색은 색 리소스로 지정해야 휴대폰 다크/라이트가 바뀔 때 다시 받지 않아도 따라 바뀐다 (안드로이드 12+)
        private fun RemoteViews.textColor(context: Context, id: Int, colorRes: Int) {
            if (Build.VERSION.SDK_INT >= 31) setColorStateList(id, "setTextColor", colorRes)
            else setTextColor(id, context.getColor(colorRes))
        }

        private fun build(context: Context): RemoteViews {
            val store = Store(context)
            val days = store.days
            val error = store.error
            val loaded = days.isNotEmpty()
            val today = LocalDate.now()
            val sunday = today.minusDays((today.dayOfWeek.value % 7).toLong())
            val v = RemoteViews(context.packageName, R.layout.widget_grass)

            // 위젯을 누르면 홈페이지, 새로고침 아이콘은 다시 읽기
            v.setOnClickPendingIntent(R.id.widget_root, PendingIntent.getActivity(context, 1,
                Intent(Intent.ACTION_VIEW, Uri.parse(HOME_URL)), PendingIntent.FLAG_IMMUTABLE))
            v.setOnClickPendingIntent(R.id.refresh, refreshIntent(context))

            v.setTextViewText(R.id.streak, if (loaded) "연속 ${Grass.streak(days, today)}일" else "")

            // 일~토 7칸
            var active = 0
            for (i in 0 until 7) {
                val d = sunday.plusDays(i.toLong())
                val day = days[d] ?: Day(0, 0)
                val future = d.isAfter(today)
                val isToday = d == today
                if (!future && day.count > 0) active++
                v.setImageViewResource(cellIds[i],
                    if (future) R.drawable.cell_future else levelCells[day.level.coerceIn(0, 4)])
                v.setViewVisibility(ringIds[i], if (isToday) View.VISIBLE else View.GONE)
                v.setTextViewText(countIds[i],
                    if (!future && day.count > 0) (if (day.count > 99) "99+" else day.count.toString()) else "")
                v.textColor(context, countIds[i], if (day.level >= 3) R.color.bg else R.color.fg)
                v.textColor(context, dayIds[i], if (isToday) R.color.fg else R.color.muted)
            }

            // 상태 줄: 오늘 기여 여부 | 이번 주 활동일
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
            v.setTextViewText(R.id.week, if (loaded) "$active/7일" else "")
            return v
        }
    }
}
