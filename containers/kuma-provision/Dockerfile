# The cluster plan Phase 6b.13 - built once here rather than
# `pip install`-ed at every deploy (compose.observability.yml's
# kuma-provision service), so a PyPI outage is a non-event instead of a
# deploy failure or, in the cluster, a crash loop.
FROM python:3.12-slim

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY provision.py .

# ENTRYPOINT rather than CMD so a caller can pass `--setup-only` through the
# pod spec's `args` alone. With this as CMD, Kubernetes `args` replaces the
# whole command line and the interpreter goes with it - the AutoKuma
# initContainer in deploy/cluster/observability/controllers/autokuma.yaml is
# the caller that needs the flag; the CronJob passes none and gets the full
# pass.
ENTRYPOINT ["python", "provision.py"]
