package com.hyatin.agentbell.pairing

import android.content.Context
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleOwner
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import java.util.EnumMap
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

class QrScannerController(
    private val context: Context,
    private val lifecycleOwner: LifecycleOwner,
    private val previewView: PreviewView,
    private val onDecoded: (String) -> Unit,
) : AutoCloseable {
    private val executor = Executors.newSingleThreadExecutor()
    private val completed = AtomicBoolean(false)
    private var cameraProvider: ProcessCameraProvider? = null

    fun start() {
        val future = ProcessCameraProvider.getInstance(context)
        future.addListener(
            {
                if (completed.get()) return@addListener
                try {
                    val provider = future.get()
                    cameraProvider = provider
                    bind(provider)
                } catch (_: Exception) {
                    // The UI retains the manual-paste fallback.
                }
            },
            ContextCompat.getMainExecutor(context),
        )
    }

    private fun bind(provider: ProcessCameraProvider) {
        val preview = Preview.Builder().build().also {
            it.surfaceProvider = previewView.surfaceProvider
        }
        val analysis = ImageAnalysis.Builder()
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
            .build()
        analysis.setAnalyzer(executor) { image -> analyze(image) }
        provider.unbindAll()
        provider.bindToLifecycle(
            lifecycleOwner,
            CameraSelector.DEFAULT_BACK_CAMERA,
            preview,
            analysis,
        )
    }

    private fun analyze(image: ImageProxy) {
        try {
            if (completed.get()) return
            val luminance = copyLuminance(image)
            val source = PlanarYUVLuminanceSource(
                luminance,
                image.width,
                image.height,
                0,
                0,
                image.width,
                image.height,
                false,
            )
            val hints = EnumMap<DecodeHintType, Any>(DecodeHintType::class.java).apply {
                put(DecodeHintType.POSSIBLE_FORMATS, listOf(BarcodeFormat.QR_CODE))
                put(DecodeHintType.CHARACTER_SET, "UTF-8")
                put(DecodeHintType.TRY_HARDER, true)
            }
            val reader = MultiFormatReader().apply { setHints(hints) }
            val result = try {
                reader.decodeWithState(BinaryBitmap(HybridBinarizer(source)))
            } catch (_: Exception) {
                null
            } finally {
                reader.reset()
            }
            if (result != null && completed.compareAndSet(false, true)) {
                ContextCompat.getMainExecutor(context).execute {
                    cameraProvider?.unbindAll()
                    onDecoded(result.text)
                }
            }
        } finally {
            image.close()
        }
    }

    override fun close() {
        completed.set(true)
        cameraProvider?.unbindAll()
        executor.shutdownNow()
    }

    companion object {
        internal fun copyLuminance(image: ImageProxy): ByteArray {
            val plane = image.planes.first()
            val buffer = plane.buffer.duplicate()
            val bufferOffset = buffer.position()
            val rowStride = plane.rowStride
            val pixelStride = plane.pixelStride
            val width = image.width
            val height = image.height
            val output = ByteArray(width * height)
            var target = 0
            for (row in 0 until height) {
                val rowStart = row * rowStride
                for (column in 0 until width) {
                    output[target++] = buffer.get(bufferOffset + rowStart + column * pixelStride)
                }
            }
            return output
        }
    }
}
