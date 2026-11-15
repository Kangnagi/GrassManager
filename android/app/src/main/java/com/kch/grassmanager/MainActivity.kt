package com.kch.grassmanager

import android.app.Activity
import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlin.concurrent.thread

/** 앱 화면: 아이디 설정, 지금 잔디 요약, 위젯 추가 */
class MainActivity : Activity() {
    private lateinit var store: Store
    private lateinit var userField: EditText
    private lateinit var summary: TextView
    private lateinit var updated: TextView
    private var loading = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        store = Store(this)
        userField = findViewById(R.id.user)
        summary = findViewById(R.id.summary)
        updated = findViewById(R.id.updated)

        userField.setText(store.user)
        userField.setOnEditorActionListener { _, action, _ ->
            if (action == EditorInfo.IME_ACTION_DONE) { save(); true } else false
        }
        findViewById<Button>(R.id.save).setOnClickListener { save() }
        findViewById<Button>(R.id.refresh).setOnClickListener { refresh() }
        findViewById<Button>(R.id.open_home).setOnClickListener {
            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(GrassWidgetProvider.HOME_URL)))
        }
        findViewById<Button>(R.id.add_widget).setOnClickListener { addWidget() }

        showSummary()
        // 처음 열었거나 마지막으로 받은 지 30분이 넘었으면 바로 다시 읽는다
        if (System.currentTimeMillis() - store.lastOk > 30 * 60 * 1000L) refresh()
    }

    private fun save() {
        val name = userField.text.toString().trim()
        if (name.isEmpty()) { toast("아이디를 입력해 주세요"); return }
        getSystemService(InputMethodManager::class.java).hideSoftInputFromWindow(userField.windowToken, 0)
        if (name != store.user) {
            store.user = name
            store.clearData()
        }
        toast("저장했어요")
        refresh()
    }

    /** 앱에서 직접 잔디를 받아 저장하고, 위젯도 다시 그린다 */
    private fun refresh() {
        if (loading) return
        loading = true
        summary.text = "불러오는 중…"
        thread {
            try {
                store.days = Grass.fetch(store.user)
                store.error = null
            } catch (e: Exception) {
                store.error = Grass.describe(e)
            }
            runOnUiThread {
                loading = false
                showSummary()
                GrassWidgetProvider.render(this)
            }
        }
    }

    private fun showSummary() {
        val days = store.days
        val error = store.error
        if (days.isEmpty()) {
            summary.text = error ?: "아직 잔디를 받지 않았어요"
            updated.text = "${store.user} 님의 잔디"
            return
        }
        val t = days[LocalDate.now()]?.count ?: 0
        summary.text = (if (t > 0) "오늘 ${t}개 기여 완료" else "오늘 아직 커밋 없음") +
            " · 연속 ${Grass.streak(days)}일"
        val time = DateTimeFormatter.ofPattern("M월 d일 HH:mm")
            .format(Instant.ofEpochMilli(store.lastOk).atZone(ZoneId.systemDefault()))
        updated.text = "${store.user} 님 · 마지막 갱신 $time" + (error?.let { " · $it" } ?: "")
    }

    /** 홈 화면에 위젯 고정 요청 (런처가 지원하면 확인 창이 뜬다) */
    private fun addWidget() {
        val manager = getSystemService(AppWidgetManager::class.java)
        if (manager.isRequestPinAppWidgetSupported) {
            manager.requestPinAppWidget(ComponentName(this, GrassWidgetProvider::class.java), null, null)
        } else {
            toast("홈 화면 빈 곳을 길게 누르고 위젯 → GrassManager를 골라 주세요")
        }
    }

    private fun toast(msg: String) = Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()
}
