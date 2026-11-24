// probe の画面: 起動したら端末を調べて出し、「5 秒圧縮して保存」で EncodeProbe(GL 経由)を動かす。出す行はすべて logcat にも出す。
// adb の口: am start -n io.github.takosasidev.campane.probe/.MainActivity --es action encode(GL 経由)/ encode-direct(直接)。
// 圧縮は onResume で始め、onPause で必ず中断してカメラとエンコーダーを閉じる。
package io.github.takosasidev.campane.probe

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.os.Bundle
import android.util.Log
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class MainActivity : Activity() {

    private lateinit var scroll: ScrollView
    private lateinit var lines: TextView
    private lateinit var button: Button
    private val worker: ExecutorService = Executors.newSingleThreadExecutor()
    private var encode: EncodeProbe? = null
    private var encodeDone: CountDownLatch? = null
    private var pendingEncode: EncodePath? = null
    private var lastPath = EncodePath.GL
    private var resumed = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        button = Button(this).apply {
            text = "5 秒圧縮して保存"
            setOnClickListener { startEncode(EncodePath.GL) }
        }
        lines = TextView(this).apply {
            typeface = Typeface.MONOSPACE
            setTextIsSelectable(true)
        }
        scroll = ScrollView(this).apply {
            fitsSystemWindows = true
            addView(LinearLayout(this@MainActivity).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(24, 24, 24, 24)
                addView(button)
                addView(lines)
            })
        }
        setContentView(scroll)

        worker.execute {
            try {
                Inspector(this, ::out).run()
            } catch (e: Exception) {
                out("調べる途中で失敗: $e")
            } finally {
                out("--- 終わり ---")
            }
        }
        if (savedInstanceState == null) pendingEncode = pathOf(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        // singleTop では onPause → onNewIntent → onResume の順に来るので、始めるのは onResume に任せる。
        pathOf(intent)?.let { pendingEncode = it }
    }

    override fun onResume() {
        super.onResume()
        resumed = true
        pendingEncode?.let {
            pendingEncode = null
            startEncode(it)
        }
    }

    override fun onPause() {
        resumed = false
        encode?.let { job ->
            job.cancel()
            // エンコーダーと GL は作業スレッドの finally で閉じる。それを上限つきで待つ。
            if (encodeDone?.await(3, TimeUnit.SECONDS) != true) out("注意: 3 秒待っても圧縮が止まりきらなかった")
        }
        super.onPause()
    }

    override fun onDestroy() {
        worker.shutdown()
        super.onDestroy()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        if (requestCode != REQUEST_CAMERA) return
        if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) {
            out("カメラの権限: 許可された")
            if (resumed) startEncode(lastPath) else pendingEncode = lastPath
        } else {
            out("カメラの権限: 断られたので圧縮しない")
            out(
                if (shouldShowRequestPermissionRationale(Manifest.permission.CAMERA)) "もう一度ボタンを押すと求め直す"
                else "「今後表示しない」の状態なので、端末の設定のアプリの権限からカメラを許可する",
            )
            out("--- 終わり ---")
        }
    }

    private fun pathOf(intent: Intent?): EncodePath? = ProbeFormat.encodePathOf(intent?.getStringExtra("action"))

    private fun startEncode(path: EncodePath) {
        lastPath = path
        if (encode != null) {
            out("圧縮はもう動いている")
            return
        }
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            out("カメラの権限が無いので求める")
            requestPermissions(arrayOf(Manifest.permission.CAMERA), REQUEST_CAMERA)
            return
        }
        val job = EncodeProbe(this, path, ::out)
        val done = CountDownLatch(1)
        encode = job
        encodeDone = done
        button.isEnabled = false
        worker.execute {
            try {
                job.run()
            } finally {
                done.countDown()
                runOnUiThread {
                    if (encode === job) encode = null
                    button.isEnabled = true
                }
            }
        }
    }

    /** 1 行 1 項目。logcat と画面の両方に出す。どのスレッドから呼んでもよい。 */
    private fun out(line: String) {
        Log.i(TAG, line)
        runOnUiThread {
            lines.append(line + "\n")
            scroll.post { scroll.fullScroll(View.FOCUS_DOWN) }
        }
    }

    private companion object {
        const val TAG = "CamPaneProbe"
        const val REQUEST_CAMERA = 1
    }
}
