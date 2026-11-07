{{- define "bookshelfng.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "bookshelfng.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "bookshelfng.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" }}
app.kubernetes.io/name: {{ include "bookshelfng.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "bookshelfng.selectorLabels" -}}
app.kubernetes.io/name: {{ include "bookshelfng.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "bookshelfng.providerMode" -}}
{{- $providerMode := lower (toString .Values.bookshelfMigration.providerMode) -}}
{{- $sourceImageTag := lower (default "" .Values.image.tag) -}}
{{- $sourceHardcover := false -}}
{{- $sourceHardcoverKnown := false -}}
{{- if regexMatch "^(hardcover|hardcover-.+|latest|main|master)$" $sourceImageTag -}}
  {{- $sourceHardcover = true -}}
  {{- $sourceHardcoverKnown = true -}}
{{- else if regexMatch "^(softcover|softcover-.+)$" $sourceImageTag -}}
  {{- $sourceHardcoverKnown = true -}}
{{- end -}}
{{- $sourceHardcoverNative := true -}}
{{- $sourceHardcoverNativeKnown := true -}}
{{- $sourceHardcoverSeen := false -}}
{{- $sourceHardcoverNativeSeen := false -}}
{{- $providerModeExplicit := ne $providerMode "" -}}
{{- range .Values.extraEnv -}}
  {{- if eq .name "HARDCOVER" -}}
    {{- if $sourceHardcoverSeen -}}
      {{- fail "extraEnv must not define HARDCOVER more than once; provider-mode inference would be ambiguous" -}}
    {{- end -}}
    {{- $sourceHardcoverSeen = true -}}
    {{- if hasKey . "value" -}}
      {{- $sourceHardcover = eq (lower (toString .value)) "true" -}}
      {{- $sourceHardcoverKnown = true -}}
    {{- else -}}
      {{- $sourceHardcoverKnown = false -}}
    {{- end -}}
  {{- else if eq .name "HARDCOVER_NATIVE" -}}
    {{- if $sourceHardcoverNativeSeen -}}
      {{- fail "extraEnv must not define HARDCOVER_NATIVE more than once; provider-mode inference would be ambiguous" -}}
    {{- end -}}
    {{- $sourceHardcoverNativeSeen = true -}}
    {{- if hasKey . "value" -}}
      {{- $sourceHardcoverNative = ne (lower (toString .value)) "false" -}}
      {{- $sourceHardcoverNativeKnown = true -}}
    {{- else -}}
      {{- $sourceHardcoverNativeKnown = false -}}
    {{- end -}}
  {{- end -}}
{{- end -}}
{{- $sourceModeKnown := or (and $sourceHardcoverKnown (not $sourceHardcover)) (and $sourceHardcoverNativeKnown (not $sourceHardcoverNative)) (and $sourceHardcoverKnown $sourceHardcover $sourceHardcoverNativeKnown $sourceHardcoverNative) -}}
{{- $sourceProviderMode := "softcover" -}}
{{- if and $sourceHardcoverKnown $sourceHardcover $sourceHardcoverNativeKnown $sourceHardcoverNative -}}
  {{- $sourceProviderMode = "hardcover" -}}
{{- end -}}
{{- if and $providerModeExplicit $sourceModeKnown -}}
  {{- if ne $providerMode $sourceProviderMode -}}
    {{- fail "bookshelfMigration.providerMode conflicts with the readable HARDCOVER settings in extraEnv; correct or remove the old overrides" -}}
  {{- end -}}
{{- end -}}
{{- if and (eq $providerMode "") $sourceModeKnown -}}
  {{- $providerMode = $sourceProviderMode -}}
{{- else if and (eq $providerMode "") (not $sourceModeKnown) -}}
  {{- fail "bookshelfMigration.providerMode must be explicit because the retained source image tag and environment do not identify the BookshelfNG provider mode" -}}
{{- end -}}
{{- if not (has $providerMode (list "hardcover" "softcover")) -}}
  {{- fail "bookshelfMigration.providerMode must be hardcover or softcover" -}}
{{- end -}}
{{- $providerMode -}}
{{- end -}}

{{- define "bookshelfng.containerEnv" -}}
{{- $providerMode := include "bookshelfng.providerMode" . | trim -}}
{{- if not (has .Values.bookshelfMigration.mode (list "automatic" "ask" "manual" "retry")) -}}
  {{- fail "bookshelfMigration.mode must be automatic, ask, manual, or retry" -}}
{{- end -}}
{{- $containerEnv := list
  (dict "name" "CHAPTARR_BOOKSHELF_COMPATIBILITY" "value" "true")
  (dict "name" "CHAPTARR_DEFAULT_PORT" "value" (toString .Values.service.port))
  (dict "name" "CHAPTARR_EXPECTED_PORT" "value" (toString .Values.service.port))
  (dict "name" "BOOKSHELFNG_MIGRATION_MODE" "value" .Values.bookshelfMigration.mode)
  (dict "name" "HARDCOVER" "value" (ternary "true" "false" (eq $providerMode "hardcover")))
  (dict "name" "HARDCOVER_NATIVE" "value" "true")
-}}
{{- range .Values.extraEnv -}}
  {{- if not (has .name (list "CHAPTARR_BOOKSHELF_COMPATIBILITY" "CHAPTARR_DEFAULT_PORT" "CHAPTARR_EXPECTED_PORT" "BOOKSHELFNG_MIGRATION_MODE" "HARDCOVER" "HARDCOVER_NATIVE")) -}}
    {{- $containerEnv = append $containerEnv . -}}
  {{- end -}}
{{- end -}}
{{- toYaml $containerEnv -}}
{{- end -}}
