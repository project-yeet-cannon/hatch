{{/*
The cluster plan Phase 5b.5 - labels every template in this chart shares, and
the one env block 5b.5 and 5b.6 (the migrate hook) render identically. Both
are named templates rather than copy-pasted blocks: a fix to the connection
string - the password-quoting note below is exactly this kind of fix - has to
land in one place, not be remembered twice.
*/}}

{{/*
Bare chart name. deploy/cluster/apps/helmrelease.yaml (5b.10) is the only
release this chart ever has, always named "aerie", so this stays equal to
.Chart.Name rather than growing a "{{ .Release.Name }}-{{ .Chart.Name }}"
pattern that would only matter if the same chart were installed twice in one
cluster - it never is.
*/}}
{{- define "aerie.name" -}}
{{- .Chart.Name -}}
{{- end -}}

{{/*
Standard Kubernetes recommended labels.
*/}}
{{- define "aerie.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{ include "aerie.selectorLabels" . }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{/*
The subset of labels that must never change across an upgrade -
Deployment/Service spec.selector is immutable, so these are also all a
template's own selector may safely include. Each workload's template adds its
own app.kubernetes.io/component on top (api, files, share, ...) so the
selectors stay distinct per workload within one release.
*/}}
{{- define "aerie.selectorLabels" -}}
app.kubernetes.io/name: {{ include "aerie.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{/*
The Postgres env block: PGUSER/PGPASSWORD from the CNPG Secret, then the two
ConnectionStrings assembled from them with Kubernetes' own $(VAR) expansion
(not Flux's ${VAR} - that would be consumed by postBuild before the pod ever
saw it). Shared verbatim by api-deployment.yaml and migrate-job.yaml (5b.6),
which is Finding 1's whole point: the migration runs the same code, against
the same connection strings, as the pods it precedes.

PGPASSWORD is single-quoted inside the connection string on purpose, even
though CNPG's own generator never produces a `;` or `=` that would otherwise
truncate the string: internal/controller/cluster_create.go calls
sethvargo/go-password's `Generate(64, 10, 0, false, true)` - 0 symbols, so
the 64-character result is letters and digits only (confirmed by reading that
call, not assumed). Quoting removes the failure mode structurally rather than
depending on an upstream generator not adding symbols later; there is no
equivalent risk on PGUSER, which is the fixed literal "aerie" from
cluster.yaml's bootstrap.initdb.owner, not a generated value.
*/}}
{{- define "aerie.postgresEnv" -}}
- name: PGUSER
  valueFrom:
    secretKeyRef:
      name: {{ .Values.postgres.secretName }}
      key: username
- name: PGPASSWORD
  valueFrom:
    secretKeyRef:
      name: {{ .Values.postgres.secretName }}
      key: password
- name: ConnectionStrings__Aerie
  value: "Host={{ .Values.postgres.host }};Port={{ .Values.postgres.port }};Database=aerie;Username=$(PGUSER);Password='$(PGPASSWORD)'"
- name: ConnectionStrings__Quartz
  value: "Host={{ .Values.postgres.host }};Port={{ .Values.postgres.port }};Database=quartz;Username=$(PGUSER);Password='$(PGPASSWORD)'"
{{- end -}}
