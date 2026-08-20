# The cluster plan Phase 6b.13 - built once here rather than
# `pip install`-ed at every deploy (compose.observability.yml's
# kuma-provision service), so a PyPI outage is a non-event instead of a
# deploy failure or, in the cluster, a crash loop.
FROM python:3.12-slim

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY provision.py .

CMD ["python", "provision.py"]
