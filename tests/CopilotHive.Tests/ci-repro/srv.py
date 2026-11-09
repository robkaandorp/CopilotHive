import http.server,sys
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(s):
        s.send_response(401); s.send_header('WWW-Authenticate','Basic realm="x"'); s.send_header('Content-Length','0'); s.end_headers()
    def log_message(*a): pass
http.server.HTTPServer(('127.0.0.1',18081),H).serve_forever()
