import React from "react";
import {
  View,
  Text,
  TextInput,
  TouchableOpacity,
  StyleSheet,
  ScrollView,
  SafeAreaView,
  KeyboardAvoidingView,
  Platform,
  StatusBar,
} from "react-native";
import { LinearGradient } from "expo-linear-gradient";
import { AntDesign, FontAwesome, Ionicons } from "@expo/vector-icons";
import { router } from "expo-router";

export default function SignupScreen() {
  return (
    <SafeAreaView style={styles.safeArea}>
      <StatusBar barStyle="dark-content" />
      <KeyboardAvoidingView
        behavior={Platform.OS === "ios" ? "padding" : "height"}
        style={{ flex: 1 }}
      >
        <ScrollView
          contentContainerStyle={styles.scrollContent}
          showsVerticalScrollIndicator={false}
        >
          <View style={styles.container}>
            <TouchableOpacity 
              style={styles.backButton}
              onPress={() => router.back()}
            >
              <Ionicons name="chevron-back" size={24} color="#0F172A" />
            </TouchableOpacity>

            <View style={styles.header}>
              <Text style={styles.title}>Create Account</Text>
              <Text style={styles.subtitle}>Join AccessCity and navigate safely</Text>
            </View>

            <View style={styles.socialRow}>
              <TouchableOpacity style={styles.socialButton}>
                <AntDesign name="google" size={24} color="#EA4335" />
              </TouchableOpacity>
              <TouchableOpacity style={styles.socialButton}>
                <FontAwesome name="facebook" size={24} color="#1877F2" />
              </TouchableOpacity>
              <TouchableOpacity style={styles.socialButton}>
                <AntDesign name="apple" size={24} color="#000" />
              </TouchableOpacity>
            </View>

            <View style={styles.dividerRow}>
              <View style={styles.dividerLine} />
              <Text style={styles.dividerText}>or signup with email</Text>
              <View style={styles.dividerLine} />
            </View>

            <View style={styles.form}>
              <View style={styles.nameRow}>
                <View style={[styles.inputContainer, { flex: 1 }]}>
                  <TextInput
                    placeholder="First Name"
                    placeholderTextColor="#94A3B8"
                    style={styles.input}
                  />
                </View>
                <View style={[styles.inputContainer, { flex: 1 }]}>
                  <TextInput
                    placeholder="Last Name"
                    placeholderTextColor="#94A3B8"
                    style={styles.input}
                  />
                </View>
              </View>

              <View style={styles.inputContainer}>
                <Ionicons name="mail-outline" size={20} color="#64748B" style={styles.inputIcon} />
                <TextInput
                  placeholder="Email Address"
                  placeholderTextColor="#94A3B8"
                  keyboardType="email-address"
                  autoCapitalize="none"
                  style={styles.input}
                />
              </View>

              <View style={styles.inputContainer}>
                <Ionicons name="lock-closed-outline" size={20} color="#64748B" style={styles.inputIcon} />
                <TextInput
                  placeholder="Password"
                  placeholderTextColor="#94A3B8"
                  secureTextEntry
                  style={styles.input}
                />
              </View>

              <View style={styles.inputContainer}>
                <Ionicons name="shield-checkmark-outline" size={20} color="#64748B" style={styles.inputIcon} />
                <TextInput
                  placeholder="Confirm Password"
                  placeholderTextColor="#94A3B8"
                  secureTextEntry
                  style={styles.input}
                />
              </View>

              <TouchableOpacity activeOpacity={0.85} style={styles.mainButtonContainer}>
                <LinearGradient
                  colors={["#2563EB", "#1D4ED8"]}
                  style={styles.mainButton}
                >
                  <Text style={styles.mainButtonText}>Sign Up</Text>
                </LinearGradient>
              </TouchableOpacity>
            </View>

            <View style={styles.footer}>
              <Text style={styles.footerText}>Already have an account? </Text>
              <TouchableOpacity onPress={() => router.push("/login")}>
                <Text style={styles.footerLink}>Log In</Text>
              </TouchableOpacity>
            </View>
          </View>
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: "#FFFFFF",
  },
  scrollContent: {
    flexGrow: 1,
  },
  container: {
    flex: 1,
    paddingHorizontal: 24,
    paddingTop: 20,
    paddingBottom: 40,
  },
  backButton: {
    width: 44,
    height: 44,
    borderRadius: 12,
    backgroundColor: "#F1F5F9",
    alignItems: "center",
    justifyContent: "center",
    marginBottom: 32,
  },
  header: {
    marginBottom: 32,
  },
  title: {
    fontSize: 32,
    fontWeight: "800",
    color: "#0F172A",
    letterSpacing: -1,
    marginBottom: 8,
  },
  subtitle: {
    fontSize: 16,
    color: "#64748B",
    fontWeight: "500",
  },
  socialRow: {
    flexDirection: "row",
    gap: 16,
    marginBottom: 24,
  },
  socialButton: {
    flex: 1,
    height: 56,
    borderRadius: 16,
    backgroundColor: "#FFFFFF",
    borderWidth: 1,
    borderColor: "#E2E8F0",
    alignItems: "center",
    justifyContent: "center",
  },
  dividerRow: {
    flexDirection: "row",
    alignItems: "center",
    marginBottom: 24,
  },
  dividerLine: {
    flex: 1,
    height: 1,
    backgroundColor: "#F1F5F9",
  },
  dividerText: {
    marginHorizontal: 16,
    fontSize: 14,
    color: "#94A3B8",
    fontWeight: "600",
  },
  form: {
    gap: 16,
  },
  nameRow: {
    flexDirection: "row",
    gap: 12,
  },
  inputContainer: {
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: "#F8FAFC",
    borderRadius: 16,
    borderWidth: 1,
    borderColor: "#E2E8F0",
    paddingHorizontal: 16,
  },
  inputIcon: {
    marginRight: 14,
  },
  input: {
    flex: 1,
    height: 56,
    fontSize: 16,
    color: "#0F172A",
    fontWeight: "600",
  },
  mainButtonContainer: {
    marginTop: 8,
    borderRadius: 20,
    shadowColor: "#2563EB",
    shadowOffset: { width: 0, height: 10 },
    shadowOpacity: 0.25,
    shadowRadius: 20,
    elevation: 6,
  },
  mainButton: {
    height: 60,
    borderRadius: 20,
    alignItems: "center",
    justifyContent: "center",
  },
  mainButtonText: {
    color: "#FFFFFF",
    fontSize: 18,
    fontWeight: "800",
    letterSpacing: 0.5,
  },
  footer: {
    flexDirection: "row",
    justifyContent: "center",
    marginTop: 32,
  },
  footerText: {
    fontSize: 15,
    color: "#64748B",
    fontWeight: "500",
  },
  footerLink: {
    fontSize: 15,
    color: "#2563EB",
    fontWeight: "700",
  },
});