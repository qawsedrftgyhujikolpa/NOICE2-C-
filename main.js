
document.addEventListener('DOMContentLoaded', () => {
    const elements = {
        dropzone: document.getElementById('dropzone'),
        fileInput: document.getElementById('fileInput'),
        viewer: document.getElementById('viewer'),
        voidImage: document.getElementById('voidImage'),
        statusText: document.getElementById('statusText'),
        logConsole: document.getElementById('logConsole'),
        resetBtn: document.getElementById('resetBtn'),
        scale: document.getElementById('settingScale'),
        speed: document.getElementById('settingSpeed'),
        audio: document.getElementById('settingAudio'),
        nitro: document.getElementById('settingNitro'),
        isColor: document.getElementById('settingColor'),
        progressContainer: document.getElementById('progressContainer'),
        progressBar: document.getElementById('progressBar'),
        progressPercent: document.getElementById('progressPercent')
    };

    let currentTempName = null;
    let currentOutputName = null;
    let audioCtx = null;
    let noiseSource = null;
    let noiseGain = null;
    let originalAudioElement = null;

    function playNoise(type) {
        stopAudio();
        if (type === 'mute') return;
        if (type === 'original' && elements.fileInput.files.length > 0) {
            if (!originalAudioElement) {
                originalAudioElement = new Audio(URL.createObjectURL(elements.fileInput.files[0]));
                originalAudioElement.loop = true;
            }
            originalAudioElement.playbackRate = parseFloat(elements.speed.value);
            originalAudioElement.play().catch(e => console.log("Audio play failed", e));
            return;
        }
        if (!audioCtx) audioCtx = new (window.AudioContext || window.webkitAudioContext)();
        if (audioCtx.state === 'suspended') audioCtx.resume();
        const bufferSize = 2 * audioCtx.sampleRate;
        const noiseBuffer = audioCtx.createBuffer(1, bufferSize, audioCtx.sampleRate);
        const output = noiseBuffer.getChannelData(0);
        let lastOut = 0;
        for (let i = 0; i < bufferSize; i++) {
            if (type === 'white') {
                output[i] = Math.random() * 2 - 1;
            } else if (type === 'brown') {
                const white = Math.random() * 2 - 1;
                output[i] = (lastOut + (0.02 * white)) / 1.02;
                lastOut = output[i];
                output[i] *= 3.5;
            }
        }
        noiseSource = audioCtx.createBufferSource();
        noiseSource.buffer = noiseBuffer;
        noiseSource.loop = true;
        noiseGain = audioCtx.createGain();
        noiseGain.gain.value = 0.1;
        noiseSource.connect(noiseGain);
        noiseGain.connect(audioCtx.destination);
        noiseSource.start();
    }

    function stopAudio() {
        if (noiseSource) { try { noiseSource.stop(); } catch (e) { } noiseSource = null; }
        if (originalAudioElement) { originalAudioElement.pause(); originalAudioElement.currentTime = 0; }
    }

    async function updateLogs() {
        try {
            const res = await fetch('/logs');
            const data = await res.json();
            if (data.logs) {
                elements.logConsole.textContent = data.logs;
                elements.logConsole.scrollTop = elements.logConsole.scrollHeight;
            }
        } catch (e) { }
    }
    setInterval(updateLogs, 2000);

    elements.fileInput.addEventListener('change', async (e) => {
        if (e.target.files.length === 0) return;
        const file = e.target.files[0];
        elements.dropzone.classList.add('hidden');
        elements.viewer.classList.remove('hidden');
        updateStatus('抽出し、消去中...', '◈');
        const formData = new FormData();
        formData.append('file', file);
        try {
            const res = await fetch('/upload', { method: 'POST', body: formData });
            const data = await res.json();
            currentTempName = data.temp_name;
            currentOutputName = data.output_name;
            startStreaming();
        } catch (err) { updateStatus('エラーが発生しました', '⚠'); }
    });

    function startStreaming() {
        if (!currentTempName) return;
        const params = new URLSearchParams({
            scale: elements.scale.value,
            is_color: elements.isColor.value,
            speed: elements.speed.value,
            nitro: elements.nitro.value
        });
        elements.voidImage.src = `/stream/${currentTempName}/${currentOutputName}?${params.toString()}&t=${Date.now()}`;
        updateStatus('虚無を投影中', '✯');
        playNoise(elements.audio.value);
    }

    elements.scale.onchange = startStreaming;
    elements.speed.onchange = () => { startStreaming(); if (originalAudioElement) originalAudioElement.playbackRate = parseFloat(elements.speed.value); };
    elements.audio.onchange = () => playNoise(elements.audio.value);
    elements.nitro.onchange = startStreaming;
    elements.isColor.onchange = startStreaming;
    elements.resetBtn.onclick = () => location.reload();

    const downloadBtn = document.getElementById('downloadBtn');
    downloadBtn.onclick = async () => {
        if (!currentTempName) return;
        downloadBtn.disabled = true;
        downloadBtn.querySelector('.btn-text').textContent = "RENDERING...";
        elements.progressContainer.classList.remove('hidden');
        elements.progressBar.style.width = '0%';
        elements.progressPercent.textContent = '0%';

        updateStatus('MP4へ具現化中...', '💾');

        // 進捗ポーリング開始
        const pollInterval = setInterval(async () => {
            try {
                const res = await fetch(`/progress/${currentOutputName}`);
                const data = await res.json();
                if (data.progress >= 0) {
                    const p = Math.round(data.progress);
                    elements.progressBar.style.width = `${p}%`;
                    elements.progressPercent.textContent = `${p}%`;
                }
            } catch (e) { }
        }, 500);

        const params = new URLSearchParams({
            scale: elements.scale.value,
            is_color: elements.isColor.value,
            audio_mode: elements.audio.value,
            nitro: elements.nitro.value
        });

        try {
            const res = await fetch(`/process_download/${currentTempName}/${currentOutputName}?${params.toString()}`);
            const data = await res.json();
            clearInterval(pollInterval);

            if (data.status === 'completed') {
                elements.progressBar.style.width = '100%';
                elements.progressPercent.textContent = '100%';
                const link = document.createElement('a');
                link.href = data.url; link.download = currentOutputName;
                document.body.appendChild(link); link.click(); document.body.removeChild(link);
                updateStatus('虚無の保存完了', '✔');
            } else { updateStatus('保存失敗', '✕'); }
        } catch (e) {
            clearInterval(pollInterval);
            updateStatus('通信エラー', '⚠');
        } finally {
            downloadBtn.disabled = false;
            downloadBtn.querySelector('.btn-text').textContent = "PROCESS & DOWNLOAD";
            setTimeout(() => elements.progressContainer.classList.add('hidden'), 3000);
        }
    };

    function updateStatus(text, icon) { elements.statusText.textContent = text; document.getElementById('statusIcon').textContent = icon; }

    elements.dropzone.addEventListener('dragover', (e) => { e.preventDefault(); elements.dropzone.style.borderColor = '#fff'; });
    elements.dropzone.addEventListener('dragleave', () => {
        elements.dropzone.style.borderColor = 'var(--accent)';
    });
    elements.dropzone.addEventListener('drop', (e) => {
        e.preventDefault();
        const files = e.dataTransfer.files;
        if (files.length > 0) { elements.fileInput.files = files; elements.fileInput.dispatchEvent(new Event('change')); }
    });
});
