// GL 経由の経路: カメラ → SurfaceTexture(OES テクスチャ)→ EGL14 でエンコーダーの入力の Surface に描く。
// GL の文脈は専用のスレッド 1 本で持ち、onFrameAvailable ごとに updateTexImage → 描く → 時刻を付けて eglSwapBuffers。
// release() は何度呼んでもよく、どのスレッドからでもよい(GL のスレッドで片付け、上限つきで待つ)。
package io.github.takosasidev.campane.probe

import android.graphics.SurfaceTexture
import android.opengl.EGL14
import android.opengl.EGLConfig
import android.opengl.EGLContext
import android.opengl.EGLDisplay
import android.opengl.EGLExt
import android.opengl.EGLSurface
import android.opengl.GLES11Ext
import android.opengl.GLES20
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.FloatBuffer
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

class GlBridge(private val encoderSurface: Surface, private val width: Int, private val height: Int) {

    private val thread = HandlerThread("CamPaneProbeGl").apply { start() }
    private val handler = Handler(thread.looper)
    private val released = AtomicBoolean(false)
    private val releaseDone = CountDownLatch(1)

    // ここから下は GL のスレッドだけが触る。
    private var display: EGLDisplay = EGL14.EGL_NO_DISPLAY
    private var context: EGLContext = EGL14.EGL_NO_CONTEXT
    private var eglSurface: EGLSurface = EGL14.EGL_NO_SURFACE
    private var texture: SurfaceTexture? = null
    private var cameraSurface: Surface? = null
    private var program = 0
    private var textureId = 0
    private val matrix = FloatArray(16)

    /** 描いた枚数と、描くのに失敗した回数(どのスレッドから読んでもよい)。 */
    val drawn = AtomicInteger()
    val failed = AtomicInteger()
    @Volatile var firstError: String? = null
        private set

    /** GL を用意し、カメラの出力先にする Surface を返す。5 秒で終わらなければ投げる。 */
    fun start(): Surface {
        val latch = CountDownLatch(1)
        var result: Surface? = null
        var error: Throwable? = null
        val posted = !released.get() && handler.post {
            try {
                result = setUp()
            } catch (t: Throwable) {
                error = t
            } finally {
                latch.countDown()
            }
        }
        if (!posted) throw IllegalStateException("GL のスレッドが止まっている")
        if (!latch.await(5, TimeUnit.SECONDS)) throw IllegalStateException("GL の用意が 5 秒で終わらなかった")
        error?.let { throw IllegalStateException("GL の用意に失敗: $it", it) }
        return result ?: throw IllegalStateException("GL の用意に失敗")
    }

    private fun setUp(): Surface {
        display = EGL14.eglGetDisplay(EGL14.EGL_DEFAULT_DISPLAY)
        check(display != EGL14.EGL_NO_DISPLAY) { "eglGetDisplay" }
        val version = IntArray(2)
        check(EGL14.eglInitialize(display, version, 0, version, 1)) { "eglInitialize ${eglError()}" }
        val attributes = intArrayOf(
            EGL14.EGL_RED_SIZE, 8, EGL14.EGL_GREEN_SIZE, 8, EGL14.EGL_BLUE_SIZE, 8, EGL14.EGL_ALPHA_SIZE, 8,
            EGL14.EGL_RENDERABLE_TYPE, EGL14.EGL_OPENGL_ES2_BIT,
            EGL_RECORDABLE_ANDROID, 1,
            EGL14.EGL_NONE,
        )
        val configs = arrayOfNulls<EGLConfig>(1)
        val count = IntArray(1)
        check(EGL14.eglChooseConfig(display, attributes, 0, configs, 0, 1, count, 0) && count[0] > 0) {
            "eglChooseConfig(EGL_RECORDABLE_ANDROID) ${eglError()}"
        }
        context = EGL14.eglCreateContext(
            display, configs[0], EGL14.EGL_NO_CONTEXT, intArrayOf(EGL14.EGL_CONTEXT_CLIENT_VERSION, 2, EGL14.EGL_NONE), 0,
        )
        check(context != EGL14.EGL_NO_CONTEXT) { "eglCreateContext ${eglError()}" }
        eglSurface = EGL14.eglCreateWindowSurface(display, configs[0], encoderSurface, intArrayOf(EGL14.EGL_NONE), 0)
        check(eglSurface != EGL14.EGL_NO_SURFACE) { "eglCreateWindowSurface ${eglError()}" }
        check(EGL14.eglMakeCurrent(display, eglSurface, eglSurface, context)) { "eglMakeCurrent ${eglError()}" }

        program = buildProgram()
        val ids = IntArray(1)
        GLES20.glGenTextures(1, ids, 0)
        textureId = ids[0]
        GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, textureId)
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MIN_FILTER, GLES20.GL_LINEAR)
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MAG_FILTER, GLES20.GL_LINEAR)
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_S, GLES20.GL_CLAMP_TO_EDGE)
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_T, GLES20.GL_CLAMP_TO_EDGE)

        val st = SurfaceTexture(textureId)
        st.setDefaultBufferSize(width, height)
        st.setOnFrameAvailableListener({ drawFrame() }, handler)
        texture = st
        return Surface(st).also { cameraSurface = it }
    }

    private fun drawFrame() {
        val st = texture ?: return
        try {
            st.updateTexImage()
            st.getTransformMatrix(matrix)
            GLES20.glViewport(0, 0, width, height)
            GLES20.glUseProgram(program)
            GLES20.glActiveTexture(GLES20.GL_TEXTURE0)
            GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, textureId)
            GLES20.glUniformMatrix4fv(GLES20.glGetUniformLocation(program, "uTex"), 1, false, matrix, 0)
            val pos = GLES20.glGetAttribLocation(program, "aPos")
            val tex = GLES20.glGetAttribLocation(program, "aTex")
            GLES20.glEnableVertexAttribArray(pos)
            GLES20.glVertexAttribPointer(pos, 2, GLES20.GL_FLOAT, false, 0, QUAD_POS)
            GLES20.glEnableVertexAttribArray(tex)
            GLES20.glVertexAttribPointer(tex, 2, GLES20.GL_FLOAT, false, 0, QUAD_TEX)
            GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4)
            // SurfaceTexture の時刻が 0 の端末では、今の時刻で代える(0 のままだとエンコーダーが捨てることがある)。
            val time = st.timestamp.takeIf { it != 0L } ?: System.nanoTime()
            EGLExt.eglPresentationTimeANDROID(display, eglSurface, time)
            if (EGL14.eglSwapBuffers(display, eglSurface)) {
                drawn.incrementAndGet()
            } else {
                failed.incrementAndGet()
                if (firstError == null) firstError = "eglSwapBuffers ${eglError()}"
            }
        } catch (e: Exception) {
            failed.incrementAndGet()
            if (firstError == null) firstError = e.toString()
        }
    }

    /** GL のスレッドで全部片付けてスレッドを止める。wait なら 2 秒まで待ち、終わらなければ false。 */
    fun release(wait: Boolean = true): Boolean {
        if (released.compareAndSet(false, true)) {
            val posted = handler.post {
                try {
                    tearDown()
                } finally {
                    releaseDone.countDown()
                }
            }
            thread.quitSafely()
            if (!posted) releaseDone.countDown()
        }
        return !wait || releaseDone.await(2, TimeUnit.SECONDS)
    }

    private fun tearDown() {
        texture?.setOnFrameAvailableListener(null)
        cameraSurface?.release()
        texture?.release()
        texture = null
        if (display != EGL14.EGL_NO_DISPLAY) {
            if (program != 0) GLES20.glDeleteProgram(program)
            if (textureId != 0) GLES20.glDeleteTextures(1, intArrayOf(textureId), 0)
            EGL14.eglMakeCurrent(display, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_CONTEXT)
            if (eglSurface != EGL14.EGL_NO_SURFACE) EGL14.eglDestroySurface(display, eglSurface)
            if (context != EGL14.EGL_NO_CONTEXT) EGL14.eglDestroyContext(display, context)
            EGL14.eglReleaseThread()
            EGL14.eglTerminate(display)
        }
        display = EGL14.EGL_NO_DISPLAY
    }

    private fun buildProgram(): Int {
        val p = GLES20.glCreateProgram()
        GLES20.glAttachShader(p, shader(GLES20.GL_VERTEX_SHADER, VERTEX))
        GLES20.glAttachShader(p, shader(GLES20.GL_FRAGMENT_SHADER, FRAGMENT))
        GLES20.glLinkProgram(p)
        val ok = IntArray(1)
        GLES20.glGetProgramiv(p, GLES20.GL_LINK_STATUS, ok, 0)
        check(ok[0] != 0) { "glLinkProgram: ${GLES20.glGetProgramInfoLog(p)}" }
        return p
    }

    private fun shader(type: Int, source: String): Int {
        val s = GLES20.glCreateShader(type)
        GLES20.glShaderSource(s, source)
        GLES20.glCompileShader(s)
        val ok = IntArray(1)
        GLES20.glGetShaderiv(s, GLES20.GL_COMPILE_STATUS, ok, 0)
        check(ok[0] != 0) { "glCompileShader: ${GLES20.glGetShaderInfoLog(s)}" }
        return s
    }

    private fun eglError(): String = "0x${Integer.toHexString(EGL14.eglGetError())}"

    private companion object {
        // EGLExt.EGL_RECORDABLE_ANDROID は API 26 の定数なので値で書く(拡張の値は 0x3142)。
        const val EGL_RECORDABLE_ANDROID = 0x3142

        const val VERTEX = """
attribute vec4 aPos;
attribute vec4 aTex;
uniform mat4 uTex;
varying vec2 vTex;
void main() {
    gl_Position = aPos;
    vTex = (uTex * aTex).xy;
}
"""
        const val FRAGMENT = """
#extension GL_OES_EGL_image_external : require
precision mediump float;
varying vec2 vTex;
uniform samplerExternalOES sTex;
void main() {
    gl_FragColor = texture2D(sTex, vTex);
}
"""
        val QUAD_POS: FloatBuffer = floats(-1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f)
        val QUAD_TEX: FloatBuffer = floats(0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f)

        fun floats(vararg v: Float): FloatBuffer =
            ByteBuffer.allocateDirect(v.size * 4).order(ByteOrder.nativeOrder()).asFloatBuffer().apply {
                put(v)
                position(0)
            }
    }
}
