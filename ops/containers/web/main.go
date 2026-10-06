package main

import (
 "context"
 "crypto/sha256"
 "crypto/subtle"
 "embed"
 "encoding/json"
 "io/fs"
 "log"
 "net/http"
 "net/http/httputil"
 "net/url"
 "os"
 "os/signal"
 "strings"
 "sync"
 "syscall"
 "time"
)

//go:embed dist security-headers.json
var assets embed.FS
func main() {
 if len(os.Args)>1 && os.Args[1]=="--healthcheck" { c:=http.Client{Timeout:3*time.Second}; r,e:=c.Get("http://127.0.0.1:8080/health/live"); if e!=nil || r.StatusCode!=200 { os.Exit(1) }; r.Body.Close(); return }
 mux:=http.NewServeMux()
 mux.HandleFunc("/health/live",func(w http.ResponseWriter,r *http.Request){ w.Header().Set("Content-Type","application/json"); w.Write([]byte(`{"status":"alive"}`)) })
 if os.Getenv("SINK_MODE")=="true" {
  key,e:=os.ReadFile("/run/secrets/fixture_key"); if e!=nil { log.Fatal("Missing fixture credential") }; expected:=sha256.Sum256([]byte(strings.TrimSpace(string(key))))
  var mutex sync.Mutex; codes:=map[string]string{}
  mux.HandleFunc("/otp",func(w http.ResponseWriter,r *http.Request){ actual:=sha256.Sum256([]byte(r.Header.Get("X-Fixture-Key"))); if subtle.ConstantTimeCompare(actual[:],expected[:])!=1 { http.NotFound(w,r); return }; mutex.Lock(); defer mutex.Unlock()
   if r.Method==http.MethodPost { var input struct { ChallengeId string `json:"challengeId"`; Code string `json:"code"` }; if json.NewDecoder(http.MaxBytesReader(w,r.Body,1024)).Decode(&input)!=nil || len(input.ChallengeId)!=36 || len(input.Code)!=6 { w.WriteHeader(400); return }; if len(codes)>1000 { w.WriteHeader(429); return }; codes[input.ChallengeId]=input.Code; w.WriteHeader(204); return }
   if r.Method==http.MethodGet { value,ok:=codes[r.URL.Query().Get("id")]; if !ok { http.NotFound(w,r); return }; json.NewEncoder(w).Encode(map[string]string{"code":value}); return }; w.WriteHeader(405)
  })
 } else {
  target,_:=url.Parse("http://api:8080"); proxy:=httputil.NewSingleHostReverseProxy(target)
  // Do not trust or forward external proxy headers in the local deployment.
  original:=proxy.Director; proxy.Director=func(r *http.Request){ original(r); r.Header.Del("Forwarded"); r.Header.Del("X-Forwarded-For"); r.Header.Del("X-Forwarded-Proto"); r.Header.Del("X-Forwarded-Host"); r.Host="api" }
  proxy.ErrorHandler=func(w http.ResponseWriter,r *http.Request,e error){ http.Error(w,"Service unavailable",503) }
  mux.Handle("/api/",proxy)
  files,_:=fs.Sub(assets,"dist"); server:=http.FileServer(http.FS(files))
  mux.HandleFunc("/",func(w http.ResponseWriter,r *http.Request){ if r.Method!=http.MethodGet && r.Method!=http.MethodHead { w.WriteHeader(405); return }; name:=strings.TrimPrefix(r.URL.Path,"/"); if name=="" { name="index.html" }; if _,e:=fs.Stat(files,name); e!=nil { if strings.Contains(name,".") { http.NotFound(w,r); return }; r.URL.Path="/" }; if r.URL.Path=="/" || strings.HasSuffix(r.URL.Path,"index.html") { w.Header().Set("Cache-Control","no-store") }; server.ServeHTTP(w,r) })
 }
 var headers map[string]string; data,_:=assets.ReadFile("security-headers.json"); if json.Unmarshal(data,&headers)!=nil { log.Fatal("Invalid security headers") }
 handler:=http.HandlerFunc(func(w http.ResponseWriter,r *http.Request){ for k,v:=range headers { w.Header().Set(k,v) }; mux.ServeHTTP(w,r) })
 server:=&http.Server{Addr:":8080",Handler:handler,ReadHeaderTimeout:10*time.Second,ReadTimeout:30*time.Second,WriteTimeout:30*time.Second,IdleTimeout:60*time.Second,MaxHeaderBytes:16384}
 done:=make(chan struct{}); stop:=make(chan os.Signal,1); signal.Notify(stop,syscall.SIGINT,syscall.SIGTERM)
 go func(){ <-stop; ctx,cancel:=context.WithTimeout(context.Background(),20*time.Second); defer cancel(); server.Shutdown(ctx); close(done) }()
 if e:=server.ListenAndServe(); e!=nil && e!=http.ErrServerClosed { log.Fatal("Web server failed") }; <-done
}
