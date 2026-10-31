$ErrorActionPreference = 'Stop'

function Read-PinnedModelEvidence {
    param([string]$Directory, [string]$FileName, [string]$Sha256)

    if ([string]::IsNullOrEmpty($FileName) -or
        $FileName -cne [IO.Path]::GetFileName($FileName) -or
        $Sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Invalid evidence catalog entry.'
    }
    $path = Join-Path $Directory $FileName
    $file = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if ($file.PSIsContainer -or
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Evidence '$FileName' must be a regular file."
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Evidence fingerprint mismatch: $FileName"
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 40
}

function Assert-ModelEvidenceReceipt {
    param($Receipt, $Profile)

    if ($null -eq $Receipt -or
        (ConvertTo-Json $Receipt -Depth 20 -Compress) -cne
        (ConvertTo-Json $Profile.reviewEvidence -Depth 20 -Compress)) {
        throw 'Cloud manifest evidence pins do not match the reviewed release catalog.'
    }
}

function Test-TinyClipReleaseEvidence {
    param([string]$BundleDirectory, $Profile)

    $pins = $Profile.reviewEvidence
    $provenance = Read-PinnedModelEvidence $BundleDirectory $Profile.provenanceFile $pins.provenanceSha256
    $model = $Profile.assets[0]
    $labels = $Profile.assets[1]
    foreach ($asset in $Profile.assets) {
        $file = Get-Item -LiteralPath (Join-Path $BundleDirectory $asset.fileName) -Force -ErrorAction Stop
        if ($file.PSIsContainer -or
            ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $file.Length -ne $asset.sizeBytes -or
            (Get-FileHash -LiteralPath $file.FullName).Hash -ne $asset.sha256) {
            throw "TinyCLIP artifact mismatch: $($asset.fileName)"
        }
    }
    $prepared = @($provenance.models | Where-Object modelId -CEQ 'tinyclip')
    if ($provenance.schemaVersion -ne 1 -or $prepared.Count -ne 1 -or
        $prepared[0].modelFile -cne $model.fileName -or
        $prepared[0].modelSha256 -ne $model.sha256 -or
        $prepared[0].modelSizeBytes -ne $model.sizeBytes -or
        $prepared[0].bundleFile -cne $labels.fileName -or
        $prepared[0].bundleSha256 -ne $labels.sha256 -or
        $provenance.executionProvider -cne 'CPUExecutionProvider' -or
        $provenance.labelCount -ne 3632 -or
        $provenance.vocabularySha256 -ne $pins.vocabularySha256 -or
        $provenance.referenceSha256 -ne $pins.referenceSha256) {
        throw 'TinyCLIP preparation evidence identity mismatch.'
    }
    $metadata = Get-Content -LiteralPath (Join-Path $BundleDirectory $labels.fileName) -Raw |
        ConvertFrom-Json -Depth 30
    if ($metadata.modelId -cne 'tinyclip' -or $metadata.modelSha256 -ne $model.sha256 -or
        $metadata.modelSizeBytes -ne $model.sizeBytes -or
        $metadata.vocabularySha256 -ne $pins.vocabularySha256 -or
        @($metadata.labels).Count -ne 3632 -or $metadata.embeddingDimension -ne 512 -or
        $metadata.precision -cne 'int8' -or $metadata.scoreKind -cne 'cosine' -or
        $metadata.inputName -cne 'pixel_values' -or $metadata.outputName -cne 'image_embeds' -or
        $metadata.inputSize -ne 224 -or $metadata.resizeMode -cne 'shortest-center-crop' -or
        $metadata.interpolation -cne 'cubic') {
        throw 'TinyCLIP calibrated bundle contract mismatch.'
    }
    foreach ($source in $Profile.sourceRevisions.PSObject.Properties) {
        if (@($metadata.sources | Where-Object {
            $_.repository -ceq $source.Name -and $_.revision -ceq $source.Value -and $_.checksumVerified -eq $true
        }).Count -eq 0) { throw 'TinyCLIP source pin mismatch.' }
    }
    $reference = Read-PinnedModelEvidence $BundleDirectory 'reference.json' $pins.referenceSha256
    if ($reference.schemaVersion -ne 1 -or $reference.vocabularySha256 -ne $pins.vocabularySha256 -or
        @($reference.fixtures).Count -ne 2) { throw 'TinyCLIP reference identity mismatch.' }
    foreach ($fixture in $reference.fixtures) {
        $result = @($fixture.models | Where-Object modelId -CEQ 'tinyclip')
        if ($result.Count -ne 1 -or $result[0].executionProvider -cne 'CPUExecutionProvider' -or
            @($result[0].scores).Count -ne 3632 -or @($result[0].imageEmbedding).Count -ne 512) {
            throw 'TinyCLIP CPU reference contract mismatch.'
        }
    }
    foreach ($name in $Profile.notices) {
        $pin = $pins.noticeSha256.PSObject.Properties[$name]
        if ($null -eq $pin) { throw 'Missing TinyCLIP notice pin.' }
        $file = Get-Item -LiteralPath (Join-Path $BundleDirectory $name) -Force -ErrorAction Stop
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $file.Length -le 0 -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $pin.Value) {
            throw "TinyCLIP notice fingerprint mismatch: $name"
        }
    }
}

function Test-RamPlusReleaseEvidence {
    param([string]$BundleDirectory, $Profile)

    $pins = $Profile.reviewEvidence
    if ($null -eq $pins -or @($pins.reports).Count -ne 2) {
        throw 'RAM++ requires reviewed provenance and CPU/DirectML evidence pins.'
    }
    $provenance = Read-PinnedModelEvidence $BundleDirectory $Profile.provenanceFile $pins.provenanceSha256
    $model = $Profile.assets[0]
    $labels = $Profile.assets[1]
    foreach ($asset in $Profile.assets) {
        $path = Join-Path $BundleDirectory $asset.fileName
        $file = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if ($file.PSIsContainer -or
            ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $file.Length -ne $asset.sizeBytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $asset.sha256) {
            throw "Release artifact mismatch: $($asset.fileName)"
        }
    }
    if ($provenance.schemaVersion -ne 1 -or
        $provenance.modelFile -cne $model.fileName -or
        $provenance.modelSha256 -ne $model.sha256 -or
        $provenance.modelSizeBytes -ne $model.sizeBytes -or
        $provenance.labelsFile -cne $labels.fileName -or
        $provenance.labelsSha256 -ne $labels.sha256 -or
        $provenance.labelsSizeBytes -ne $labels.sizeBytes -or
        $provenance.sourceRevision -cne $Profile.sourceRevisions.'recognize-anything' -or
        $provenance.checkpointRevision -cne $Profile.sourceRevisions.'recognize-anything-plus-model' -or
        $provenance.checkpoints[0].sha256 -ne $pins.checkpointSha256 -or
        $provenance.labelCount -ne 4585 -or
        $provenance.license -cne $Profile.license -or
        $null -eq $provenance.externalDataFiles -or
        @($provenance.externalDataFiles).Count -ne 0) {
        throw 'Export provenance does not match the reviewed artifact identity.'
    }
    if ($provenance.input.name -cne 'image' -or
        $provenance.input.dtype -cne 'float32' -or
        ($provenance.input.shape -join ',') -ne '1,3,384,384' -or
        $provenance.preprocessing.layout -cne 'NCHW' -or
        ($provenance.preprocessing.resize -join ',') -ne '384,384' -or
        ($provenance.preprocessing.normalizationMean -join ',') -ne '0.485,0.456,0.406' -or
        ($provenance.preprocessing.normalizationStd -join ',') -ne '0.229,0.224,0.225' -or
        @($provenance.outputs).Count -ne 3) {
        throw 'Export input/preprocessing contract mismatch.'
    }
    foreach ($name in @('logits', 'probabilities', 'targets')) {
        $output = @($provenance.outputs | Where-Object name -CEQ $name)
        if ($output.Count -ne 1 -or $output[0].dtype -cne 'float32' -or
            ($output[0].shape -join ',') -ne '1,4585') {
            throw 'Export output contract mismatch.'
        }
    }
    foreach ($name in $Profile.notices) {
        $notice = @($provenance.redistributionNotices | Where-Object file -CEQ $name)
        if ($notice.Count -ne 1) { throw "Missing notice binding: $name" }
        $pinnedNotice = $pins.noticeSha256.PSObject.Properties[$name]
        if ($null -eq $pinnedNotice -or $pinnedNotice.Value -ne $notice[0].sha256) {
            throw "Notice does not match reviewed catalog: $name"
        }
        $path = Join-Path $BundleDirectory $name
        $file = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if ($file.PSIsContainer -or $file.Length -le 0 -or
            ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $notice[0].sha256) {
            throw "Notice fingerprint mismatch: $name"
        }
    }
    $providers = @($pins.reports.executionProvider | Sort-Object)
    if (($providers -join ',') -cne 'CPUExecutionProvider,DmlExecutionProvider') {
        throw 'Both CPU and DirectML review reports are required.'
    }
    $reviewedInputs = $null
    foreach ($pin in $pins.reports) {
        $report = Read-PinnedModelEvidence $BundleDirectory $pin.fileName $pin.sha256
        if ($report.schemaVersion -ne 1 -or
            $report.executionProvider -cne $pin.executionProvider -or
            $report.sessionProviders -cnotcontains $pin.executionProvider -or
            $report.modelSha256 -ne $model.sha256 -or
            $report.modelSizeBytes -ne $model.sizeBytes -or
            $report.labelsSha256 -ne $labels.sha256 -or
            $report.sourceRevision -cne $provenance.sourceRevision -or
            $report.checkpointRevision -cne $provenance.checkpointRevision -or
            $report.checkpointSha256 -ne $pins.checkpointSha256 -or
            $report.validatorSha256 -ne $pins.validatorSha256 -or
            $report.passed -cne $true -or
            $report.exactTargetParity -cne $true -or
            $report.finiteOutputs -cne $true -or
            $report.rawTolerancePassed -cne $true -or
            $report.rawTolerance.rtol -ne 0.001 -or
            $report.rawTolerance.atol -ne 0.0001 -or
            @($report.images).Count -ne 8 -or
            @($report.images.imageSha256 | Select-Object -Unique).Count -ne 8) {
            throw "Parity report identity or summary mismatch: $($pin.fileName)"
        }
        foreach ($image in $report.images) {
            if ($image.imageSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
                $image.labelCount -ne 4585 -or
                $null -eq $image.targetMismatchCount -or $image.targetMismatchCount -ne 0 -or
                $image.finite -cne $true -or $image.binaryTargets -cne $true -or
                $image.rawTolerancePassed -cne $true) {
                throw "Parity input failed: $($pin.fileName)"
            }
        }
        $inputs = ($report.images.imageSha256 | Sort-Object) -join ','
        if ($null -ne $reviewedInputs -and $reviewedInputs -cne $inputs) {
            throw 'CPU and DirectML evidence must cover the same inputs.'
        }
        $reviewedInputs = $inputs
    }
}
