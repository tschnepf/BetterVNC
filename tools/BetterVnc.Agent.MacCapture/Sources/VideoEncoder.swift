import CoreMedia
import CoreVideo
import Foundation
import VideoToolbox

enum Codec: String {
    case h264
    case hevc

    var type: CMVideoCodecType { self == .hevc ? kCMVideoCodecType_HEVC : kCMVideoCodecType_H264 }
    var label: String { self == .hevc ? "HEVC" : "H.264" }
}

/// A VideoToolbox compression session set up for a remote desktop: real time, no frame reordering (every frame leaves
/// the encoder as soon as it is done), a bitrate with a cap on bursts, one key frame at the start and then only on
/// request. Hardware when the Mac has it, which every Mac that runs ScreenCaptureKit does for H.264.
final class VideoEncoder {
    /// Largest picture either codec is asked for: H.264 level 5.1/5.2 (36864 macroblocks), which the hardware encoders take.
    static let maxWidth = 4096
    static let maxHeight = 2304

    let codec: Codec
    let width: Int
    let height: Int
    let isHardware: Bool
    let lowLatency: Bool
    private let session: VTCompressionSession
    private let output: (CMSampleBuffer) -> Void

    init(codec: Codec, width: Int, height: Int, bitrateKbps: Int, fps: Int, allowLowLatency: Bool,
         output: @escaping (CMSampleBuffer) -> Void) throws {
        // Low-latency rate control (H.264) is made for exactly this kind of stream: steady frame sizes, no frame held
        // back. Not every Mac offers it, so it is asked for first and the ordinary real-time mode taken otherwise. Where
        // it only comes in software (seen in a macOS virtual machine) an ordinary hardware session wins.
        var best: (session: VTCompressionSession, lowLatency: Bool, hardware: Bool)?
        var status: OSStatus = noErr
        for low in (allowLowLatency && codec == .h264) ? [true, false] : [false] {
            var specification: [CFString: Any] = [kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder: true]
            if low { specification[kVTVideoEncoderSpecification_EnableLowLatencyRateControl] = true }
            var created: VTCompressionSession?
            status = VTCompressionSessionCreate(
                allocator: kCFAllocatorDefault, width: Int32(width), height: Int32(height), codecType: codec.type,
                encoderSpecification: specification as CFDictionary, imageBufferAttributes: nil,
                compressedDataAllocator: nil, outputCallback: nil, refcon: nil, compressionSessionOut: &created)
            guard status == noErr, let session = created else { continue }
            let hardware = VideoEncoder.copyBool(session, kVTCompressionPropertyKey_UsingHardwareAcceleratedVideoEncoder)
            if let previous = best, previous.hardware || !hardware {
                VTCompressionSessionInvalidate(session)
            } else {
                if let previous = best { VTCompressionSessionInvalidate(previous.session) }
                best = (session, low, hardware)
            }
            if best?.hardware == true { break }
        }
        guard let chosen = best else {
            throw HelperError("VideoToolbox could not create a \(codec.label) encoder for \(width)x\(height) (error \(status)).")
        }
        self.codec = codec
        self.width = width
        self.height = height
        self.session = chosen.session
        self.lowLatency = chosen.lowLatency
        self.isHardware = chosen.hardware
        self.output = output

        set(kVTCompressionPropertyKey_RealTime, kCFBooleanTrue)
        set(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse)
        switch codec {
        case .hevc: set(kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_HEVC_Main_AutoLevel)
        case .h264: set(kVTCompressionPropertyKey_ProfileLevel,
                        lowLatency ? kVTProfileLevel_H264_ConstrainedHigh_AutoLevel : kVTProfileLevel_H264_High_AutoLevel)
        }
        // One key frame at the start, then only when the viewer asks (after a lost frame), as the Windows agent does.
        set(kVTCompressionPropertyKey_MaxKeyFrameInterval, NSNumber(value: 1_000_000))
        // Pictures go in as BGRA; the encoder converts them with BT.601, limited range, which is what the viewer's
        // decoder assumes for a stream that says nothing about colour. The stream says it anyway.
        set(kVTCompressionPropertyKey_ColorPrimaries, kCVImageBufferColorPrimaries_ITU_R_709_2)
        set(kVTCompressionPropertyKey_TransferFunction, kCVImageBufferTransferFunction_ITU_R_709_2)
        set(kVTCompressionPropertyKey_YCbCrMatrix, kCVImageBufferYCbCrMatrix_ITU_R_601_4)
        setRates(bitrateKbps: bitrateKbps, fps: fps)
        VTCompressionSessionPrepareToEncodeFrames(session)
    }

    deinit {
        VTCompressionSessionInvalidate(session)
    }

    /// Bitrate and frame rate can change while the session runs.
    func setRates(bitrateKbps: Int, fps: Int) {
        let bitsPerSecond = max(bitrateKbps, 100) * 1000
        set(kVTCompressionPropertyKey_AverageBitRate, NSNumber(value: bitsPerSecond))
        // A cap on bursts (a scrolled page, a new window): at most one and a half seconds' worth of the average in any
        // one second, so a big change cannot pile up on the link behind it.
        set(kVTCompressionPropertyKey_DataRateLimits, [NSNumber(value: bitsPerSecond / 8 * 3 / 2), NSNumber(value: 1.0)] as CFArray)
        set(kVTCompressionPropertyKey_ExpectedFrameRate, NSNumber(value: fps))
    }

    /// Hands one picture to the encoder; false when the session broke (sleep, a GPU change) and has to be replaced.
    func encode(_ pixels: CVPixelBuffer, at time: CMTime, keyframe: Bool) -> Bool {
        let options: CFDictionary? = keyframe ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary : nil
        let output = self.output
        let status = VTCompressionSessionEncodeFrame(
            session, imageBuffer: pixels, presentationTimeStamp: time, duration: .invalid,
            frameProperties: options, infoFlagsOut: nil
        ) { status, _, sample in
            if status == noErr, let sample { output(sample) }
        }
        if status != noErr { logLine("\(codec.label) encoder refused a frame (error \(status))") }
        return status == noErr
    }

    @discardableResult
    private func set(_ key: CFString, _ value: CFTypeRef) -> Bool {
        let status = VTSessionSetProperty(session, key: key, value: value)
        if status != noErr { logLine("\(codec.label) encoder: \(key as String) not taken (error \(status))") }
        return status == noErr
    }

    private static func copyBool(_ session: VTCompressionSession, _ key: CFString) -> Bool {
        var value: Unmanaged<CFTypeRef>?
        guard VTSessionCopyProperty(session, key: key, allocator: nil, valueOut: &value) == noErr,
              let object = value?.takeRetainedValue() else { return false }
        return (object as? NSNumber)?.boolValue ?? false
    }
}

/// VideoToolbox hands out H.264/HEVC as length-prefixed NAL units with the parameter sets in the format description;
/// the viewer wants an Annex B stream (start codes) with the parameter sets in front of every key frame.
enum AnnexB {
    private static let startCode: [UInt8] = [0, 0, 0, 1]

    static func accessUnit(from sample: CMSampleBuffer, codec: Codec) -> (bytes: [UInt8], keyframe: Bool)? {
        guard let block = CMSampleBufferGetDataBuffer(sample), let format = CMSampleBufferGetFormatDescription(sample) else { return nil }
        let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[String: Any]]
        let keyframe = !((attachments?.first?[kCMSampleAttachmentKey_NotSync as String] as? Bool) ?? false)

        var setCount = 0
        var lengthSize: Int32 = 4
        _ = parameterSet(format, codec, 0, nil, nil, &setCount, &lengthSize)
        var out = [UInt8]()
        if keyframe {
            for index in 0..<setCount {
                var pointer: UnsafePointer<UInt8>?
                var size = 0
                if parameterSet(format, codec, index, &pointer, &size, nil, nil) == noErr, let pointer {
                    out += startCode
                    out.append(contentsOf: UnsafeBufferPointer(start: pointer, count: size))
                }
            }
        }

        let length = CMBlockBufferGetDataLength(block)
        guard length > 0 else { return nil }
        var data = [UInt8](repeating: 0, count: length)
        let copied = data.withUnsafeMutableBytes { raw in
            CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: length, destination: raw.baseAddress!)
        }
        guard copied == noErr else { return nil }
        out.reserveCapacity(out.count + length + 16)
        let prefix = Int(lengthSize)
        var offset = 0
        while offset + prefix <= length {
            var size = 0
            for i in 0..<prefix { size = size << 8 | Int(data[offset + i]) }
            offset += prefix
            guard size > 0, offset + size <= length else { break }
            out += startCode
            out.append(contentsOf: data[offset..<(offset + size)])
            offset += size
        }
        return (out, keyframe)
    }

    private static func parameterSet(
        _ format: CMFormatDescription, _ codec: Codec, _ index: Int,
        _ pointer: UnsafeMutablePointer<UnsafePointer<UInt8>?>?, _ size: UnsafeMutablePointer<Int>?,
        _ count: UnsafeMutablePointer<Int>?, _ lengthSize: UnsafeMutablePointer<Int32>?
    ) -> OSStatus {
        switch codec {
        case .h264:
            return CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                format, parameterSetIndex: index, parameterSetPointerOut: pointer, parameterSetSizeOut: size,
                parameterSetCountOut: count, nalUnitHeaderLengthOut: lengthSize)
        case .hevc:
            return CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(
                format, parameterSetIndex: index, parameterSetPointerOut: pointer, parameterSetSizeOut: size,
                parameterSetCountOut: count, nalUnitHeaderLengthOut: lengthSize)
        }
    }
}
